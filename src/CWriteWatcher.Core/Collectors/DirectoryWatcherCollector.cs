using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Filtering;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace CWriteWatcher.Core.Collectors;

/// <summary>
/// 标准模式采集器：ReadDirectoryChangesW + 文件大小差分估算。免管理员权限。
/// </summary>
/// <remarks>
/// <para><b>能力边界（务必在 UI 中如实告知用户）：</b></para>
/// <list type="bullet">
///   <item>写入量是<b>估算值</b>（两次大小快照的差值），不是真实单次写入字节数。</item>
///   <item><b>拿不到发起进程</b>（ProcessId = 0），因此进程级节流不生效、提醒中显示"未知进程"。</item>
///   <item>原地覆盖写（大小不变）<b>完全漏报</b>。</item>
///   <item>缓冲区溢出（ERROR_NOTIFY_ENUM_DIR）会丢事件。**丢了多少条不可知**，
///         只能计数；该计数会显示在设置页与诊断信息里，非零即代表日志不完整。</item>
/// </list>
/// <para><b>性能设计：</b>枚举阶段直接跳过命中排除表的目录子树（WinSxS / SoftwareDistribution /
/// Packages 等），可省下数千个句柄；缓冲区大小按目录数自适应分配，总量受
/// <see cref="MonitorScope.BufferBudgetMb"/> 约束。</para>
/// </remarks>
public sealed class DirectoryWatcherCollector : IFileWriteCollector
{
    private sealed class DirWatcher
    {
        /// <summary>串行化「完成回调重挂」与「关闭释放」，避免二者竞态。</summary>
        public readonly object Sync = new();

        public string Path = string.Empty;
        public SafeFileHandle Handle = null!;
        public ThreadPoolBoundHandle Bound = null!;
        public byte[] Buffer = Array.Empty<byte>();
        public GCHandle BufferPin;
        public IntPtr BufferPtr;
        public unsafe NativeOverlapped* Overlapped;
        public volatile bool Disposed;
    }

    private readonly struct SizeBaseline
    {
        public readonly long Size;
        public readonly long Tick;

        public SizeBaseline(long size, long tick)
        {
            Size = size;
            Tick = tick;
        }
    }

    private readonly List<DirWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SizeBaseline> _baselines = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig? _config;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private ExclusionMatcher? _matcher;

    private long _hits;
    private long _misses;
    private long _overflowCount;
    private long _bufferBytes;
    private int _outstanding;
    private long _baselineOps;
    private bool _truncated;

    private volatile bool _running;
    private volatile string _status = "已停止";

    public CollectorMode Mode => CollectorMode.Standard;
    public bool IsRunning => _running;
    public string StatusText => _status;
    public Action<FileWriteEvent>? OnEvent { get; set; }
    public event Action<int, string>? Progress;

    public void Start(AppConfig config)
    {
        if (_running) return;

        _config = config;
        _matcher = ExclusionMatcher.Build(config, Environment.ProcessId);
        _cts = new CancellationTokenSource();
        _running = true;
        _status = "正在枚举目录…";
        _worker = Task.Factory.StartNew(() => RunAsync(_cts.Token), _cts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }

        ShutdownWatchers();
        _status = "已停止";
    }

    private void RunAsync(CancellationToken ct)
    {
        try
        {
            var dirs = EnumerateScope(ct);
            if (ct.IsCancellationRequested) return;

            _status = $"正在打开 {dirs.Count} 个目录句柄…";

            var bufferSize = ComputeBufferSize(dirs.Count, _config!);

            var opened = 0;
            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) break;
                if (TryCreateWatcher(dir, bufferSize)) opened++;
            }

            _status = _truncated
                ? $"运行中 · 监控 {opened} 个目录（已达上限，范围被截断）"
                : $"运行中 · 监控 {opened} 个目录";

            Progress?.Invoke(100, _status);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _status = "启动失败：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 范围枚举

    private List<string> EnumerateScope(CancellationToken ct)
    {
        var cfg = _config!;
        var scope = cfg.Scope;
        var cap = Math.Max(100, scope.MaxMonitoredDirectories);
        var result = new List<string>(Math.Min(cap, 4096));
        var stack = new Stack<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = 0;

        if (scope.WholeDriveC)
        {
            stack.Push((@"C:\", 0));
        }
        else
        {
            foreach (var inc in scope.IncludeDirectories)
            {
                var p = PathUtil.Normalize(inc);
                if (p.Length > 0 && Directory.Exists(p)) stack.Push((p, 0));
            }

            if (stack.Count == 0)
            {
                // v0.2 §5.2 默认范围
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile)) stack.Push((PathUtil.Normalize(userProfile), 0));

                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(programData)) stack.Push((PathUtil.Normalize(programData), 0));

                // 系统目录只做浅层覆盖：用户装了软件、系统更新时能看到，同时避免深层递归的句柄成本
                var shallow = Math.Max(1, scope.ShallowDepthForSystemDirs);
                var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (var name in new[] { "Program Files", "Program Files (x86)", "Windows" })
                {
                    var p = name == "Windows"
                        ? sysRoot
                        : Path.Combine(Path.GetPathRoot(sysRoot) ?? @"C:\", name);
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) stack.Push((PathUtil.Normalize(p), -shallow));
                }
            }
        }

        // 深度编码：>=0 表示不限深度（0 为起点），<0 表示剩余浅层层数
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            if (result.Count >= cap) { _truncated = true; break; }

            var (path, depth) = stack.Pop();
            if (!visited.Add(path)) continue;

            if (!PathUtil.IsOnDriveC(path)) continue;

            // 命中排除表的目录整棵子树跳过 —— 这是句柄数量能压下来的关键
            if (_matcher is not null && _matcher.IsExcludedPath(path.AsSpan())) continue;

            result.Add(path);

            if (++reported % 2000 == 0)
            {
                Progress?.Invoke(-1, $"正在枚举目录… 已发现 {result.Count} 个");
            }

            if (depth < 0)
            {
                if (depth == -1) continue; // 浅层到底
                depth += 1;
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(path))
                {
                    if (ct.IsCancellationRequested) break;
                    stack.Push((sub, depth));
                }
            }
            catch (UnauthorizedAccessException) { /* 跳过无权访问的目录 */ }
            catch (DirectoryNotFoundException) { /* 竞态：目录已删除 */ }
            catch (PathTooLongException) { /* 跳过超长路径 */ }
            catch (IOException) { /* 跳过 */ }
        }

        return result;
    }

    private static int ComputeBufferSize(int dirCount, AppConfig cfg)
    {
        var budgetBytes = (long)Math.Max(4, cfg.Scope.BufferBudgetMb) * 1024 * 1024;
        var maxPerDir = Math.Max(4, cfg.Scope.BufferSizeKb) * 1024;

        if (dirCount <= 0) return maxPerDir;

        var perDir = budgetBytes / dirCount;
        perDir = Math.Clamp(perDir, 1024, maxPerDir);

        // 向上取到 1 KB 的整数倍
        return (int)((perDir + 1023) / 1024 * 1024);
    }

    // ---------------------------------------------------------------- 句柄管理

    private unsafe bool TryCreateWatcher(string dir, int bufferSize)
    {
        SafeFileHandle? handle = null;
        ThreadPoolBoundHandle? bound = null;
        GCHandle pin = default;
        var watcher = new DirWatcher { Path = dir };

        try
        {
            handle = NativeMethods.CreateFileW(
                dir,
                NativeMethods.FILE_LIST_DIRECTORY,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }

            bound = ThreadPoolBoundHandle.BindHandle(handle);

            var buffer = new byte[bufferSize];
            pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            watcher.Handle = handle;
            watcher.Bound = bound;
            watcher.Buffer = buffer;
            watcher.BufferPin = pin;
            watcher.BufferPtr = pin.AddrOfPinnedObject();

            watcher.Overlapped = bound.AllocateNativeOverlapped(OnIoCompleted, watcher, buffer);

            if (!IssueRead(watcher))
            {
                CleanupWatcher(watcher);
                return false;
            }

            lock (_gate)
            {
                _watchers.Add(watcher);
                _bufferBytes += bufferSize;
            }

            return true;
        }
        catch
        {
            if (pin.IsAllocated) pin.Free();
            bound?.Dispose();
            handle?.Dispose();
            return false;
        }
    }

    private unsafe bool IssueRead(DirWatcher w)
    {
        if (w.Disposed) return false;

        Interlocked.Increment(ref _outstanding);

        var ok = NativeMethods.ReadDirectoryChangesW(
            w.Handle,
            (byte*)w.BufferPtr,
            w.Buffer.Length,
            false,
            NativeMethods.WATCH_FILTER,
            out _,
            w.Overlapped,
            IntPtr.Zero);

        if (ok) return true;

        var err = Marshal.GetLastWin32Error();
        if (err == 997) return true; // ERROR_IO_PENDING

        Interlocked.Decrement(ref _outstanding);
        return false;
    }

    private unsafe void OnIoCompleted(uint errorCode, uint numBytes, NativeOverlapped* pOverlapped)
    {
        Interlocked.Decrement(ref _outstanding);

        var w = ThreadPoolBoundHandle.GetNativeOverlappedState(pOverlapped) as DirWatcher;
        if (w is null) return;

        lock (w.Sync)
        {
            if (w.Disposed) return;

            try
            {
                if (errorCode == 0 && numBytes > 0)
                {
                    ProcessBuffer(w, (int)numBytes);
                }
                else if (errorCode == NativeMethods.ERROR_NOTIFY_ENUM_DIR)
                {
                    // 缓冲区溢出：这一批变更已经丢了。
                    // 条数不可知（内核只说"枚举目录重来"），所以这里只能计**次数**，
                    // 并让它出现在设置页与诊断信息里 —— 静默丢数据比慢一点严重得多。
                    Interlocked.Increment(ref _overflowCount);
                }
                else if (errorCode == NativeMethods.ERROR_OPERATION_ABORTED)
                {
                    return; // 正在停止
                }

                if (w.Disposed || !_running) return;

                // ★ ThreadPoolBoundHandle 分配的 NativeOverlapped 是「一次性」的。
                //   同一个指针绝不能用于第二次异步操作，否则运行时会抛：
                //     InvalidOperationException: NativeOverlapped cannot be reused for multiple operations.
                //   （见 ThreadPoolBoundHandleOverlapped.CompletionCallback 的 _completed 检查）
                //   正确做法：先分配新的挂载下一轮，再释放刚刚完成的那一个。
                var next = w.Bound.AllocateNativeOverlapped(OnIoCompleted, w, w.Buffer);
                w.Overlapped = next;

                if (!IssueRead(w))
                {
                    // 句柄已失效（目录被删除 / 卷被卸载）：回收并让该 watcher 退出
                    w.Bound.FreeNativeOverlapped(next);
                    w.Overlapped = null;
                    w.Disposed = true;
                    return;
                }

                w.Bound.FreeNativeOverlapped(pOverlapped);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RDCW] 完成回调异常 {w.Path}：{ex.Message}");
            }
        }
    }

    private unsafe void ProcessBuffer(DirWatcher w, int bytes)
    {
        var basePtr = (byte*)w.BufferPtr;
        var offset = 0;

        while (offset + 12 <= bytes)
        {
            var info = *(NativeMethods.FILE_NOTIFY_INFORMATION*)(basePtr + offset);
            if (info.FileNameLength > 0 && offset + 12 + info.FileNameLength <= bytes)
            {
                var name = NativeMethods.ReadFileName(basePtr, offset + 12, info.FileNameLength);
                try
                {
                    HandleNotification(w, info.Action, name);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RDCW] 处理通知失败 {name}：{ex.Message}");
                }
            }

            if (info.NextEntryOffset == 0) break;
            offset += info.NextEntryOffset;
        }
    }

    private void HandleNotification(DirWatcher w, int action, string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        var full = w.Path.EndsWith('\\') ? w.Path + name : w.Path + "\\" + name;
        var path = PathUtil.Normalize(full);
        if (path.Length == 0 || !PathUtil.IsOnDriveC(path)) return;

        var now = Environment.TickCount64;

        switch (action)
        {
            case NativeMethods.FILE_ACTION_ADDED:
            {
                if (!NativeMethods.TryGetFileInfo(path, out var data, out var isDir)) return;
                if (isDir)
                {
                    TryAddDirectory(path);
                    return;
                }

                _baselines[path] = new SizeBaseline(data.Size, now);
                Emit(path, data.Size, WriteKind.Create);
                break;
            }

            case NativeMethods.FILE_ACTION_MODIFIED:
            {
                if (!NativeMethods.TryGetFileInfo(path, out var data, out var isDir)) return;
                if (isDir) return;

                var current = data.Size;

                if (!_baselines.TryGetValue(path, out var baseline))
                {
                    // 首次见到该文件（程序启动前就已存在）：只能建立基线，无法知道原始大小
                    _baselines[path] = new SizeBaseline(current, now);
                    Interlocked.Increment(ref _misses);
                    return;
                }

                var delta = current - baseline.Size;
                if (delta < 0) delta = current; // 被截断后重写

                _baselines[path] = new SizeBaseline(current, now);

                if (delta > 0)
                {
                    Interlocked.Increment(ref _hits);
                    Emit(path, delta, WriteKind.Modify);
                }

                SweepBaselines(now);
                break;
            }

            case NativeMethods.FILE_ACTION_RENAMED_NEW_NAME:
            {
                if (NativeMethods.TryGetFileInfo(path, out var data, out var isDir) && !isDir)
                {
                    _baselines[path] = new SizeBaseline(data.Size, now);
                }
                break;
            }

            case NativeMethods.FILE_ACTION_REMOVED:
                _baselines.TryRemove(path, out _);
                break;
        }
    }

    private void TryAddDirectory(string dir)
    {
        if (!_running || _config is null) return;

        // 新增目录也纳入监控（否则新建目录下的写入会被漏掉）
        lock (_gate)
        {
            if (_watchers.Count >= _config.Scope.MaxMonitoredDirectories) { _truncated = true; return; }
        }

        if (_matcher is not null && _matcher.IsExcludedPath(dir.AsSpan())) return;

        var bufferSize = _watchers.Count > 0 ? _watchers[0].Buffer.Length : 64 * 1024;
        TryCreateWatcher(dir, bufferSize);
    }

    private void Emit(string path, long size, WriteKind kind)
    {
        var e = new FileWriteEvent(
            timestampTicks: DateTime.UtcNow.Ticks,
            path: path,
            size: size,
            windowKey: PathUtil.StableHash(path),
            processId: 0,                 // 标准模式拿不到发起进程
            processName: null,
            sizeIsExact: false,           // 差分估算
            kind: kind,
            source: CollectorMode.Standard);

        OnEvent?.Invoke(e);
    }

    private void SweepBaselines(long now)
    {
        if ((Interlocked.Increment(ref _baselineOps) & 0x3FF) != 0) return; // 每 1024 次扫一遍
        if (_baselines.Count < 20_000) return;

        var cutoff = now - 300_000; // 5 分钟未活动即淘汰
        var stale = new List<string>(256);
        foreach (var kv in _baselines)
        {
            if (kv.Value.Tick < cutoff) stale.Add(kv.Key);
            if (stale.Count >= 5000) break;
        }
        foreach (var k in stale) _baselines.TryRemove(k, out _);
    }

    // ---------------------------------------------------------------- 关闭

    private unsafe void ShutdownWatchers()
    {
        List<DirWatcher> snapshot;
        lock (_gate)
        {
            snapshot = new List<DirWatcher>(_watchers);
            _watchers.Clear();
        }

        foreach (var w in snapshot)
        {
            lock (w.Sync)
            {
                w.Disposed = true;
                try
                {
                    if (w.Overlapped != null)
                    {
                        NativeMethods.CancelIoEx(w.Handle, w.Overlapped);
                    }
                }
                catch { /* ignore */ }
            }
        }

        // 等待所有完成回调退出，避免在回调仍持有指针时释放内存
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _outstanding) > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
        }

        var safeToFree = Volatile.Read(ref _outstanding) <= 0;

        foreach (var w in snapshot)
        {
            lock (w.Sync)
            {
                try
                {
                    if (safeToFree && w.Overlapped != null)
                    {
                        w.Bound.FreeNativeOverlapped(w.Overlapped);
                        w.Overlapped = null;
                    }
                    w.Bound.Dispose();
                    w.Handle.Dispose();
                    if (w.BufferPin.IsAllocated) w.BufferPin.Free();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RDCW] 释放 {w.Path} 失败：{ex.Message}");
                }
            }
        }

        _bufferBytes = 0;
        _baselines.Clear();
    }

    private unsafe void CleanupWatcher(DirWatcher w)
    {
        try
        {
            w.Disposed = true;
            if (w.Overlapped != null)
            {
                w.Bound.FreeNativeOverlapped(w.Overlapped);
                w.Overlapped = null;
            }
            w.Bound.Dispose();
            w.Handle.Dispose();
            if (w.BufferPin.IsAllocated) w.BufferPin.Free();
        }
        catch { /* ignore */ }
    }

    public CollectorStats GetStats()
    {
        int count;
        lock (_gate) count = _watchers.Count;

        return new CollectorStats(
            count,
            count,
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _overflowCount),
            Interlocked.Read(ref _bufferBytes),
            _truncated);
    }

    public void Dispose() => Stop();
}

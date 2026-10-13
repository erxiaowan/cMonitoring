using System.Runtime.InteropServices;
using CWriteWatcher.Core.Collectors;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Filtering;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Util;
using CWriteWatcher.Etw.Native;
using CWriteWatcher.Etw.Parsing;

namespace CWriteWatcher.Etw;

/// <summary>
/// 精确模式采集器：ETW <c>Microsoft-Windows-Kernel-File</c> 实时会话。
/// </summary>
/// <remarks>
/// <para><b>需要管理员权限。</b>内核类 provider 的实时会话对非提升进程返回 ERROR_ACCESS_DENIED，
/// 这是 Windows 的硬性限制，没有用户态绕过办法。</para>
/// <para><b>能力：</b>精确的单次写入字节数（Write 事件的 Size）、精确的进程（事件头自带 ProcessId）、
/// 单个会话覆盖整个卷（无句柄爆炸问题）。</para>
/// <para><b>为什么在采集层就做排除：</b>被排除的路径占事件总量的 90% 以上，
/// 在回调里直接丢弃可以避免它们进入 Channel，是 CPU 能压进 2% 的关键。</para>
/// <para><b>路径过滤无法在内核侧完成</b>：<c>FileKey</c> 要查表才能映射成路径，
/// 所以排除逻辑只能在用户态尽早执行，且必须做到"零分配丢弃"。</para>
/// </remarks>
public sealed class EtwKernelFileCollector : IFileWriteCollector
{
    private const string SessionName = "CWriteWatcher-KernelFile";
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;

    private readonly EventSchemaCache _schemas = new();
    private readonly ProcessNameCache _processNames = new();

    private FileKeyPathMap _pathMap = new();
    private ExclusionMatcher? _matcher;
    private AppConfig? _config;

    private IntPtr _propsBuffer = IntPtr.Zero;
    private IntPtr _loggerNamePtr = IntPtr.Zero;
    private EtwNative.EventRecordCallback? _recordCallback;
    private Thread? _processThread;

    private ulong _sessionHandle;
    private ulong _traceHandle;

    /// <summary>会话属性的"模板"。每次 QUERY 前要重新写回缓冲区（QUERY 会覆盖它）。</summary>
    private EtwNative.EVENT_TRACE_PROPERTIES _propsTemplate;

    /// <summary>保护 <see cref="_propsBuffer"/> —— QUERY 线程与 Stop() 都会写它。</summary>
    private readonly object _propsLock = new();

    /// <summary>周期性拉取会话统计的定时器（读 EventsLost）。</summary>
    private Timer? _statsTimer;

    /// <summary>ETW 报告因缓冲区不足而丢弃的事件条数。</summary>
    private long _eventsLost;

    /// <summary>ETW 报告实时消费者来不及取走而丢弃的缓冲区数。</summary>
    private long _realTimeBuffersLost;

    /// <summary>刚创建的文件（FileKey → 时间戳）。用于模式 A 的"仅新建文件"判定。</summary>
    private readonly Dictionary<ulong, long> _newFileKeys = new();

    private long _eventCount;
    private long _writeCount;
    private long _excludedCount;
    private long _unknownPathCount;
    private long _lastSweepTick;
    private long _schemaMissCount;

    /// <summary>Write 事件里 FileKey 与 FileObject 相等的次数（用于确认两个键空间是否同一套）。</summary>
    private long _keyEqualsObject;
    private long _keyDiffersFromObject;

    // 关键事件的到达计数。映射表为空时，看这几行就能立刻定位是哪一类事件没来。
    private long _nameCreateCount;      // 10 —— FileKey→路径 的来源
    private long _nameDeleteCount;      // 11
    private long _createCount;          // 12
    private long _closeCount;           // 13 + 14
    private long _createNewFileCount;   // 30 —— 新建文件的权威信号

    private void CountEventId(int eventId)
    {
        switch (eventId)
        {
            case EtwNative.EVENT_ID_NAME_CREATE: Interlocked.Increment(ref _nameCreateCount); break;
            case EtwNative.EVENT_ID_NAME_DELETE: Interlocked.Increment(ref _nameDeleteCount); break;
            case EtwNative.EVENT_ID_CREATE: Interlocked.Increment(ref _createCount); break;
            case EtwNative.EVENT_ID_CLEANUP:
            case EtwNative.EVENT_ID_CLOSE: Interlocked.Increment(ref _closeCount); break;
            case EtwNative.EVENT_ID_CREATE_NEW_FILE: Interlocked.Increment(ref _createNewFileCount); break;
        }
    }

    private volatile bool _running;
    private volatile string _status = "已停止";

    public CollectorMode Mode => CollectorMode.Precise;
    public bool IsRunning => _running;
    public string StatusText => _status;
    public Action<FileWriteEvent>? OnEvent { get; set; }
    public event Action<int, string>? Progress;

    public FileKeyPathMap PathMap => _pathMap;
    public long ExcludedCount => Interlocked.Read(ref _excludedCount);
    public long UnknownPathCount => Interlocked.Read(ref _unknownPathCount);

    /// <summary>
    /// ETW 报告的丢失事件条数（<c>EventsLost</c> + <c>RealTimeBuffersLost</c>）。
    /// </summary>
    /// <remarks>
    /// <para><b>非零即代表日志不完整</b> —— 用户看到的写入记录少了，而事件本身
    /// 在 ETW 里就已经被丢弃，事后无法补回。</para>
    /// <para>触发条件通常是瞬时写入洪峰（例如整盘复制）超过了
    /// <c>MinimumBuffers=4 / MaximumBuffers=16 × 128 KB</c> 的缓冲能力。</para>
    /// </remarks>
    public long LostEvents =>
        Interlocked.Read(ref _eventsLost) + Interlocked.Read(ref _realTimeBuffersLost);

    /// <summary>精确模式是否可用（是否具备管理员权限）。</summary>
    public static bool IsAvailable => ProcessNameCache.IsElevated();

    public void Start(AppConfig config)
    {
        if (_running) return;

        if (!IsAvailable)
        {
            _status = "启动失败：精确模式需要管理员权限（请以管理员身份运行，或改用标准模式）";
            Progress?.Invoke(-1, _status);
            return;
        }

        _config = config;
        _matcher = ExclusionMatcher.Build(config, Environment.ProcessId);
        _pathMap = new FileKeyPathMap(PathUtil.BuildDeviceMap());

        try
        {
            if (!StartSession(out var error))
            {
                _status = "启动失败：" + error;
                Progress?.Invoke(-1, _status);
                CleanupNative();
                return;
            }
        }
        catch (Exception ex)
        {
            _status = "启动失败：" + ex.Message;
            Progress?.Invoke(-1, _status);
            CleanupNative();
            return;
        }

        _running = true;
        _status = "运行中 · ETW 实时会话已建立";
        Progress?.Invoke(100, _status);

        // 周期性拉取会话统计，读 EventsLost。
        // ★ 为什么必须做：ETW 在缓冲区不足时会**静默丢事件** —— 会话照常跑、不报错、
        //   我们的日志却少了条目。不主动查询，用户永远不知道自己看到的是残缺的。
        //   30 秒一次：足以在"开始丢"时及时反映，开销可忽略。
        _statsTimer = new Timer(_ => QuerySessionStats(), null, 30_000, 30_000);
    }

    /// <summary>
    /// 用 <c>ControlTrace(QUERY)</c> 刷新会话属性，把 <c>EventsLost</c> /
    /// <c>RealTimeBuffersLost</c> 读出来。
    /// </summary>
    /// <remarks>
    /// <para>实时会话跑起来之后，这两个计数器**只存在于 EVENT_TRACE_PROPERTIES 里**，
    /// 而只有 QUERY 会把它们写回我们提供的缓冲区。没有别的途径能拿到。</para>
    /// <para>取的是**累计值**（ETW 不会清零），所以直接覆盖而不是累加。</para>
    /// </remarks>
    private void QuerySessionStats()
    {
        if (!_running || _propsBuffer == IntPtr.Zero) return;

        lock (_propsLock)
        {
            try
            {
                // QUERY 会覆盖缓冲区，先把模板和会话名还原。
                Marshal.StructureToPtr(_propsTemplate, _propsBuffer, false);
                EtwNative.WriteString(_propsBuffer, (int)_propsTemplate.LoggerNameOffset, SessionName);

                var status = EtwNative.ControlTraceW(
                    0, SessionName, _propsBuffer, EtwNative.EVENT_TRACE_CONTROL_QUERY);
                if (status != EtwNative.ERROR_SUCCESS) return;

                var q = Marshal.PtrToStructure<EtwNative.EVENT_TRACE_PROPERTIES>(_propsBuffer);
                Interlocked.Exchange(ref _eventsLost, q.EventsLost);
                Interlocked.Exchange(ref _realTimeBuffersLost, q.RealTimeBuffersLost);
            }
            catch
            {
                // 查询失败绝不影响采集
            }
        }
    }

    private bool StartSession(out string error)
    {
        error = string.Empty;

        var propsSize = EtwNative.PropertiesSize(SessionName);
        _propsBuffer = Marshal.AllocHGlobal(propsSize);
        for (var i = 0; i < propsSize; i++) Marshal.WriteByte(_propsBuffer, i, 0);

        var props = new EtwNative.EVENT_TRACE_PROPERTIES
        {
            Wnode = new EtwNative.WNODE_HEADER
            {
                BufferSize = (uint)propsSize,
                Flags = EtwNative.WNODE_FLAG_TRACED_GUID,
            },
            BufferSize = 128,          // 每个缓冲区 128 KB
            MinimumBuffers = 4,
            MaximumBuffers = 16,
            LogFileMode = EtwNative.EVENT_TRACE_REAL_TIME_MODE,
            FlushTimer = 1,
            LoggerNameOffset = (uint)Marshal.SizeOf<EtwNative.EVENT_TRACE_PROPERTIES>(),
            LogFileNameOffset = 0,
        };

        Marshal.StructureToPtr(props, _propsBuffer, false);
        EtwNative.WriteString(_propsBuffer, (int)props.LoggerNameOffset, SessionName);

        // 留一份模板：ControlTrace(QUERY) 会把查询结果写回同一块缓冲区，
        // 每次查询前必须先把模板（含 BufferSize / LoggerNameOffset）还原回去。
        _propsTemplate = props;

        // 清理可能残留的同名会话（上次异常退出）
        var status = EtwNative.StartTraceW(out _sessionHandle, SessionName, _propsBuffer);
        if (status == EtwNative.ERROR_ALREADY_EXISTS)
        {
            EtwNative.ControlTraceW(0, SessionName, _propsBuffer, EtwNative.EVENT_TRACE_CONTROL_STOP);
            Marshal.StructureToPtr(props, _propsBuffer, false);
            EtwNative.WriteString(_propsBuffer, (int)props.LoggerNameOffset, SessionName);
            status = EtwNative.StartTraceW(out _sessionHandle, SessionName, _propsBuffer);
        }

        if (status != EtwNative.ERROR_SUCCESS)
        {
            error = status == EtwNative.ERROR_ACCESS_DENIED
                ? "拒绝访问（需要管理员权限）"
                : $"StartTrace 失败，错误码 {status}";
            return false;
        }

        var enableStatus = EtwNative.EnableTraceEx2(
            _sessionHandle,
            EtwNative.KernelFileProviderId,
            EVENT_CONTROL_CODE_ENABLE_PROVIDER,
            TRACE_LEVEL_VERBOSE,
            EtwNative.SUBSCRIBE_KEYWORDS,
            0,
            0,
            IntPtr.Zero);

        if (enableStatus != EtwNative.ERROR_SUCCESS)
        {
            error = enableStatus == EtwNative.ERROR_ACCESS_DENIED
                ? "启用 Kernel-File provider 被拒绝（需要管理员权限）"
                : $"EnableTraceEx2 失败，错误码 {enableStatus}";
            EtwNative.ControlTraceW(_sessionHandle, null, _propsBuffer, EtwNative.EVENT_TRACE_CONTROL_STOP);
            return false;
        }

        _recordCallback = OnEventRecord;
        _loggerNamePtr = Marshal.StringToHGlobalUni(SessionName);

        var logfile = new EtwNative.EVENT_TRACE_LOGFILE
        {
            LoggerName = _loggerNamePtr,
            LogFileName = IntPtr.Zero,
            ProcessTraceMode = EtwNative.PROCESS_TRACE_MODE_REAL_TIME | EtwNative.PROCESS_TRACE_MODE_EVENT_RECORD,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_recordCallback),
            BufferCallback = IntPtr.Zero,
        };

        _traceHandle = EtwNative.OpenTraceW(ref logfile);
        if (_traceHandle == ulong.MaxValue)
        {
            error = $"OpenTrace 失败，错误码 {Marshal.GetLastWin32Error()}";
            EtwNative.ControlTraceW(_sessionHandle, null, _propsBuffer, EtwNative.EVENT_TRACE_CONTROL_STOP);
            return false;
        }

        _processThread = new Thread(ProcessTraceLoop)
        {
            IsBackground = true,
            Name = "CWriteWatcher-ETW",
            Priority = ThreadPriority.AboveNormal,
        };
        _processThread.Start();

        return true;
    }

    private void ProcessTraceLoop()
    {
        try
        {
            var handles = new[] { _traceHandle };
            var status = EtwNative.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (status != EtwNative.ERROR_SUCCESS && _running)
            {
                _status = $"ProcessTrace 退出，错误码 {status}";
            }
        }
        catch (Exception ex)
        {
            _status = "ETW 会话异常：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 事件处理

    private void OnEventRecord(IntPtr pEventRecord)
    {
        try
        {
            Interlocked.Increment(ref _eventCount);

            var eventId = Marshal.ReadInt16(pEventRecord, Offsets.EventDescriptorId);
            CountEventId(eventId);
            var processId = Marshal.ReadInt32(pEventRecord, Offsets.ProcessId);
            var threadId = Marshal.ReadInt32(pEventRecord, Offsets.ThreadId);
            var timeStamp = Marshal.ReadInt64(pEventRecord, Offsets.TimeStamp);
            var userData = Marshal.ReadIntPtr(pEventRecord, Offsets.UserData);
            var userDataLength = (ushort)Marshal.ReadInt16(pEventRecord, Offsets.UserDataLength);

            // ---------- 自身 PID 排除（第一道闸门）----------
            if (_config?.Exclusions.ExcludeSelfProcess == true && processId == Environment.ProcessId) return;

            var schema = _schemas.Get(pEventRecord, eventId);
            if (schema is null)
            {
                Interlocked.Increment(ref _schemaMissCount);
                return;
            }

            if (!PayloadWalker.TryParse(userData, userDataLength, schema, out var fields)) return;

            var now = Environment.TickCount64;

            // ★ 分发逻辑抽到 KernelFileEventRouter（纯函数、可单元测试）。
            //   曾经这段逻辑直接写在这里，导致 Create 事件用 FileKey 当键（而 Create 没有 FileKey），
            //   映射表永远是空的 —— 见 KernelFileEventRouter 的注释。
            var route = KernelFileEventRouter.Route(eventId, in fields);

            switch (route.Action)
            {
                case KernelFileAction.NameSet:
                    foreach (var k in route.Keys) _pathMap.Set(k, fields.FileName, now);
                    return;

                case KernelFileAction.NewFile:
                    foreach (var k in route.Keys)
                    {
                        _pathMap.Set(k, fields.FileName, now);
                        _newFileKeys[k] = now;
                    }
                    return;

                case KernelFileAction.NameRemove:
                    foreach (var k in route.Keys)
                    {
                        _pathMap.Remove(k);
                        _newFileKeys.Remove(k);
                    }
                    return;

                case KernelFileAction.Truncate:
                    // 截断到 0 字节 → 视为"不再是刚新建的文件"
                    if (fields.ExtraInformation == 0)
                    {
                        foreach (var k in route.Keys) _newFileKeys.Remove(k);
                    }
                    return;

                case KernelFileAction.Write:
                    HandleWrite(route, processId, threadId, timeStamp, in fields, now);
                    return;

                default:
                    return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ETW] 事件回调异常：{ex.Message}");
        }
    }

    /// <summary>按顺序尝试两个键解析路径，并分别计数（诊断用）。</summary>
    private string? ResolvePath(in KernelFileRoute route, long now)
    {
        if (route.PrimaryKey != 0 && _pathMap.TryGet(route.PrimaryKey, now, out var p1)) return p1;
        if (route.SecondaryKey != 0 && _pathMap.TryGet(route.SecondaryKey, now, out var p2)) return p2;
        return null;
    }

    private void HandleWrite(in KernelFileRoute route, int pid, int tid, long timeStamp, in ParsedFields fields, long now)
    {
        if (route.PrimaryKey == 0 && route.SecondaryKey == 0) return;
        if (fields.Size <= 0) return;

        Interlocked.Increment(ref _writeCount);

        // 记录 FileKey 与 FileObject 是否相等 —— 这决定了映射表的键空间设计是否成立。
        if (fields.FileKey != 0 && fields.FileObject != 0)
        {
            if (fields.FileKey == fields.FileObject) Interlocked.Increment(ref _keyEqualsObject);
            else Interlocked.Increment(ref _keyDiffersFromObject);
        }

        var path = ResolvePath(in route, now);
        if (path is null) Interlocked.Increment(ref _unknownPathCount);

        // 排除过滤（在采集层就丢弃，避免进入 Channel —— 这是 CPU 能压住的关键）
        if (!string.IsNullOrEmpty(path))
        {
            if (_matcher is not null && _matcher.IsExcludedPath(path.AsSpan()))
            {
                Interlocked.Increment(ref _excludedCount);
                foreach (var k in route.Keys) _pathMap.MarkExcluded(k, now);   // 释放字符串，只留标记（v0.2 §2.6）
                return;
            }
        }

        var processName = _processNames.GetName(pid);
        if (_matcher is not null && _matcher.IsExcludedProcess(processName)) return;

        var isNewFile = (route.PrimaryKey != 0 && _newFileKeys.ContainsKey(route.PrimaryKey))
                        || (route.SecondaryKey != 0 && _newFileKeys.ContainsKey(route.SecondaryKey));

        // windowKey 用 FileKey（文件的持久标识）——阈值口径是"同一文件在窗口内累计"，
        // 用 FileObject 会把同一文件的两次打开拆成两组。FileKey 缺失时退回 FileObject。
        var windowKey = fields.FileKey != 0 ? fields.FileKey : unchecked((long)route.PrimaryKey);

        var e = new FileWriteEvent(
            timestampTicks: FileTimeToUtcTicks(timeStamp),
            path: path,
            size: fields.Size,
            windowKey: windowKey,
            processId: pid,
            processName: processName,
            sizeIsExact: true,
            kind: isNewFile ? WriteKind.Create : WriteKind.Modify,
            source: CollectorMode.Precise,
            threadId: tid,
            offset: fields.Offset);

        OnEvent?.Invoke(e);

        SweepIfNeeded(now);
    }

    private static long FileTimeToUtcTicks(long fileTime)
    {
        try
        {
            return fileTime <= 0 ? DateTime.UtcNow.Ticks : DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch
        {
            return DateTime.UtcNow.Ticks;
        }
    }

    private void SweepIfNeeded(long now)
    {
        if (now - _lastSweepTick < 10_000) return;
        _lastSweepTick = now;

        if (_newFileKeys.Count > 0)
        {
            var cutoff = now - 60_000;
            List<ulong>? stale = null;
            foreach (var kv in _newFileKeys)
            {
                if (kv.Value < cutoff) (stale ??= new List<ulong>()).Add(kv.Key);
            }
            if (stale is not null)
            {
                foreach (var k in stale) _newFileKeys.Remove(k);
            }
        }
    }

    // ---------------------------------------------------------------- 停止

    public void Stop()
    {
        if (!_running && _traceHandle == 0 && _sessionHandle == 0) return;

        // 停表之前先取一次最终统计 —— 否则最后那 30 秒里丢的事件就永远漏记了。
        QuerySessionStats();

        _running = false;
        _statsTimer?.Dispose();
        _statsTimer = null;

        try
        {
            if (_traceHandle != 0 && _traceHandle != ulong.MaxValue)
            {
                EtwNative.CloseTrace(_traceHandle);
                _traceHandle = 0;
            }
        }
        catch { /* ignore */ }

        try { _processThread?.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _processThread = null;

        try
        {
            lock (_propsLock)
            {
                if (_sessionHandle != 0 && _propsBuffer != IntPtr.Zero)
                {
                    EtwNative.ControlTraceW(_sessionHandle, null, _propsBuffer, EtwNative.EVENT_TRACE_CONTROL_STOP);
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            _sessionHandle = 0;
        }

        CleanupNative();
        _status = "已停止";
    }

    private void CleanupNative()
    {
        try
        {
            if (_loggerNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerNamePtr); _loggerNamePtr = IntPtr.Zero; }
            if (_propsBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_propsBuffer); _propsBuffer = IntPtr.Zero; }
        }
        catch { /* ignore */ }

        _recordCallback = null;
    }

    public CollectorStats GetStats() => new(
        MonitoredDirectories: 0,             // ETW 是卷级会话，没有"监控目录数"的概念
        HandleCount: 1,
        PathMapHits: _pathMap.Hits,
        PathMapMisses: _pathMap.Misses,
        LostEvents: LostEvents,
        BufferBytes: 0,
        TruncatedByLimit: false);

    /// <summary>
    /// 导出已发现的事件 schema，用于核对字段布局。
    /// </summary>
    /// <remarks>
    /// 这是排查"精确模式读不到路径 / 大小"这类问题的第一手段：
    /// 如果报告里某个 Event ID 的字段列表与预期不符，说明该 Windows 版本的 manifest 有变化。
    /// </remarks>
    public string BuildSchemaReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已构建 schema 数量：{_schemas.SchemaBuildCount}    构建失败：{_schemas.SchemaFailureCount}");
        sb.AppendLine($"事件总数：{Interlocked.Read(ref _eventCount)}    Write 事件：{Interlocked.Read(ref _writeCount)}");
        sb.AppendLine($"被排除丢弃：{Interlocked.Read(ref _excludedCount)}    未知路径：{Interlocked.Read(ref _unknownPathCount)}");
        sb.AppendLine($"路径映射：命中 {_pathMap.Hits} / 未命中 {_pathMap.Misses}（命中率 {_pathMap.HitRate:0.##}%），当前条目 {_pathMap.Count}");
        sb.AppendLine();

        // ---- 映射表健康度 ----
        // 这几行是排查"精确模式收不到路径"的核心：
        //   映射表条目为 0 而 NameCreate/Create 计数也为 0 → 对应事件的关键字没订阅；
        //   条目为 0 但 Create 计数很大              → 建映射时用错了键（历史缺陷）。
        var nameCreate = Interlocked.Read(ref _nameCreateCount);
        var create = Interlocked.Read(ref _createCount);
        var newFile = Interlocked.Read(ref _createNewFileCount);
        sb.AppendLine("=== 映射表健康度 ===");
        sb.AppendLine($"NameCreate(10)：{nameCreate}    NameDelete(11)：{Interlocked.Read(ref _nameDeleteCount)}");
        sb.AppendLine($"Create(12)：{create}    Close/Cleanup(13/14)：{Interlocked.Read(ref _closeCount)}");
        sb.AppendLine($"CreateNewFile(30)：{newFile}   ← 为 0 说明模式 A 的\"仅提醒新建文件\"永远不触发");
        sb.AppendLine($"FileKey 与 FileObject：相同 {Interlocked.Read(ref _keyEqualsObject)} / 不同 {Interlocked.Read(ref _keyDiffersFromObject)}");

        if (_pathMap.Count == 0 && create > 0)
        {
            sb.AppendLine("!! 映射表为空但 Create 事件到达了 —— 说明建映射时用错了键（Create 只有 FileObject，没有 FileKey）。");
        }
        else if (_pathMap.Count == 0 && nameCreate > 0)
        {
            sb.AppendLine("!! 映射表为空但 NameCreate 事件到达了 —— 检查路径转换（NT 设备路径 → 盘符路径）是否失败。");
        }
        sb.AppendLine();

        foreach (var kv in _schemas.All)
        {
            var schema = kv.Value;
            if (schema is null)
            {
                sb.AppendLine($"[ID {kv.Key}] <schema 构建失败>");
                continue;
            }

            sb.AppendLine($"[ID {kv.Key}] {EtwNative.EventIdName(kv.Key)}");
            foreach (var f in schema.Fields)
            {
                sb.AppendLine($"    {f.Name}  (InType={f.InType})");
            }
        }

        if (_schemas.All.Count == 0)
        {
            sb.AppendLine("（尚未捕获到任何事件。若刚启动，请等待几秒后再导出。）");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        Stop();
        _schemas.Dispose();
    }

    /// <summary>
    /// 通过 <see cref="Marshal.OffsetOf"/> 在运行时计算结构体字段偏移，
    /// 避免手写偏移量写错导致读出垃圾数据。
    /// </summary>
    private static class Offsets
    {
        public static readonly int EventDescriptorId =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.EventHeader))
            + (int)Marshal.OffsetOf<EtwNative.EVENT_HEADER>(nameof(EtwNative.EVENT_HEADER.EventDescriptor))
            + (int)Marshal.OffsetOf<EtwNative.EVENT_DESCRIPTOR>(nameof(EtwNative.EVENT_DESCRIPTOR.Id));

        public static readonly int ProcessId =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.EventHeader))
            + (int)Marshal.OffsetOf<EtwNative.EVENT_HEADER>(nameof(EtwNative.EVENT_HEADER.ProcessId));

        public static readonly int ThreadId =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.EventHeader))
            + (int)Marshal.OffsetOf<EtwNative.EVENT_HEADER>(nameof(EtwNative.EVENT_HEADER.ThreadId));

        public static readonly int TimeStamp =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.EventHeader))
            + (int)Marshal.OffsetOf<EtwNative.EVENT_HEADER>(nameof(EtwNative.EVENT_HEADER.TimeStamp));

        public static readonly int UserData =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.UserData));

        public static readonly int UserDataLength =
            (int)Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.UserDataLength));
    }
}

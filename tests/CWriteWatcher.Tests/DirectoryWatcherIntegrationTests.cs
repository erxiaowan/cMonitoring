using CWriteWatcher.Core.Collectors;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Util;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>
/// 标准模式采集器的真实文件系统集成测试。
/// </summary>
/// <remarks>
/// <para><b>回归目标：</b><c>ThreadPoolBoundHandle.AllocateNativeOverlapped</c> 返回的
/// <c>NativeOverlapped*</c> 是<b>一次性</b>的 —— 同一个指针用于第二次异步操作时，运行时会抛
/// <c>InvalidOperationException: NativeOverlapped cannot be reused for multiple operations</c>，
/// 而且是在线程池完成回调里抛出，属于未处理异常，会<b>直接让整个进程崩溃</b>。</para>
/// <para>重挂下一轮读请求必须「先分配新的、再释放旧的」。这类缺陷纯单元测试覆盖不到
/// （所以最初 88 个测试全绿、程序却一启动就挂），必须靠真实句柄的集成测试兜住。</para>
/// </remarks>
public class DirectoryWatcherIntegrationTests
{
    /// <summary>
    /// 挑一个位于 C 盘、且不被内置排除表命中的工作目录。
    /// 注意不能用 <see cref="Path.GetTempPath"/> —— <c>%LOCALAPPDATA%\Temp\*</c> 本身就在排除表里。
    /// </summary>
    private static string? PickBaseDirOnC()
    {
        foreach (var c in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     @"C:\",
                 })
        {
            if (!string.IsNullOrEmpty(c) && PathUtil.IsOnDriveC(c) && Directory.Exists(c)) return c;
        }
        return null;
    }

    private static AppConfig ConfigFor(string dir)
    {
        var cfg = new AppConfig();
        cfg.Scope.WholeDriveC = false;
        cfg.Scope.IncludeDirectories.Clear();
        cfg.Scope.IncludeDirectories.Add(dir);
        cfg.Scope.MaxMonitoredDirectories = 64;
        return cfg;
    }

    private static void WaitForWatchers(DirectoryWatcherCollector c, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (c.GetStats().MonitoredDirectories == 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void 连续写入应当持续收到事件_不因NativeOverlapped复用而崩溃()
    {
        var baseDir = PickBaseDirOnC();
        if (baseDir is null) return; // 非 C 盘环境，跳过

        var dir = Path.Combine(baseDir, "CWT-IT-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var events = new List<FileWriteEvent>();
            using var collector = new DirectoryWatcherCollector
            {
                OnEvent = e => { lock (events) events.Add(e); },
            };

            collector.Start(ConfigFor(dir));
            WaitForWatchers(collector);

            Assert.True(collector.GetStats().MonitoredDirectories > 0,
                "应当至少挂载一个目录句柄（若为 0，说明枚举或建句柄阶段就失败了）");

            // 连续多轮写入：每一轮都会走一次「完成回调 → 重挂下一轮读请求」，
            // 正是原先崩溃的路径。写 6 轮要求重挂链至少成功 6 次。
            for (var i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"t{i}.txt"), new string('x', 8192));
                Thread.Sleep(150);
            }

            var deadline = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < deadline)
            {
                lock (events) { if (events.Count >= 6) break; }
                Thread.Sleep(50);
            }

            collector.Stop();

            int count;
            lock (events) count = events.Count;

            var stats = collector.GetStats();
            Assert.True(count >= 6,
                $"应当至少收到 6 个写入事件，实际 {count} 个（偏少说明完成回调重挂链已断裂）。" +
                $"诊断：状态=\"{collector.StatusText}\"，监控目录={stats.MonitoredDirectories}，" +
                $"句柄={stats.HandleCount}，缓冲区={stats.BufferBytes}，丢失事件={stats.LostEvents}");

            // 事件内容基本合理性
            FileWriteEvent sample;
            lock (events) sample = events[0];
            Assert.True(PathUtil.IsOnDriveC(sample.Path), "事件路径应当在 C 盘");
            Assert.Equal(CollectorMode.Standard, sample.Source);
            Assert.False(sample.SizeIsExact, "标准模式的写入量必须是估算值");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void 停止后应当完全释放_并可反复启动停止()
    {
        var baseDir = PickBaseDirOnC();
        if (baseDir is null) return;

        var dir = Path.Combine(baseDir, "CWT-IT2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            for (var round = 0; round < 3; round++)
            {
                using var collector = new DirectoryWatcherCollector { OnEvent = _ => { } };

                collector.Start(ConfigFor(dir));
                WaitForWatchers(collector);
                Assert.True(collector.IsRunning);

                File.WriteAllText(Path.Combine(dir, $"r{round}.txt"), "hello");
                Thread.Sleep(300);

                collector.Stop();

                Assert.False(collector.IsRunning);
                Assert.Equal(0, collector.GetStats().MonitoredDirectories);
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void 停止后应当能删除被监控目录_说明没有残留独占句柄()
    {
        var baseDir = PickBaseDirOnC();
        if (baseDir is null) return;

        var dir = Path.Combine(baseDir, "CWT-IT3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "data");

        var collector = new DirectoryWatcherCollector { OnEvent = _ => { } };
        try
        {
            collector.Start(ConfigFor(dir));
            WaitForWatchers(collector);
            collector.Stop();

            // 句柄必须以 FILE_SHARE_DELETE 打开且及时释放，否则这里会抛 IOException
            var ex = Record.Exception(() => Directory.Delete(dir, true));
            Assert.Null(ex);
        }
        finally
        {
            collector.Dispose();
            TryDelete(dir);
        }
    }

    private static void TryDelete(string dir)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return;
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
    }
}

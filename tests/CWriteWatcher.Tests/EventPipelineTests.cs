using CWriteWatcher.Core.Collectors;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Filtering;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>端到端管道测试：验证各闸门的顺序与抑制原因是否正确。</summary>
public class EventPipelineTests
{
    private static AppConfig Config(Action<AppConfig>? mutate = null)
    {
        var cfg = new AppConfig
        {
            TriggerMode = TriggerMode.Over10MB,
            ThresholdScope = ThresholdScope.SameFileWindow,
        };

        // 播种默认排除表（与 ConfigStore 的行为一致）
        cfg.Exclusions.Rules.AddRange(DefaultExclusions.BuildPathRules());
        cfg.Exclusions.ExcludedProcesses.AddRange(DefaultExclusions.BuildProcessNames());

        mutate?.Invoke(cfg);
        return cfg;
    }

    private static FileWriteEvent Evt(
        string path,
        long size,
        int pid = 4242,
        string? process = "myapp.exe",
        WriteKind kind = WriteKind.Modify,
        CollectorMode source = CollectorMode.Precise)
        => new(
            timestampTicks: DateTime.UtcNow.Ticks,
            path: path,
            size: size,
            windowKey: Core.Util.PathUtil.StableHash(path),
            processId: pid,
            processName: process,
            sizeIsExact: source == CollectorMode.Precise,
            kind: kind,
            source: source,
            threadId: 1,
            offset: 0);

    private static List<AlertRecord> Run(AppConfig cfg, params FileWriteEvent[] events)
    {
        var raised = new List<AlertRecord>();
        using var p = new EventPipeline(cfg, selfProcessId: 999_999);

        p.AlertRaised += r => raised.Add(r);
        p.Start();

        foreach (var e in events) p.Post(in e);

        // 等待消费完成
        var deadline = Environment.TickCount64 + 3000;
        while (p.Stats.Received < events.Length && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
        Thread.Sleep(50);

        return raised;
    }

    [Fact]
    public void 自身进程写入应当被排除()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Users\a\file.bin", 20 * 1024 * 1024, pid: 999_999));

        Assert.Empty(raised);
    }

    [Fact]
    public void 黑名单进程写入应当被排除()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Users\a\file.bin", 20 * 1024 * 1024, process: "MsMpEng.exe"));

        Assert.Empty(raised);
    }

    [Fact]
    public void 排除路径写入应当被丢弃()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Windows\Temp\big.bin", 100 * 1024 * 1024));

        Assert.Empty(raised);
    }

    [Fact]
    public void 低于阈值不应提醒()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Users\a\small.bin", 1024 * 1024));

        Assert.Empty(raised);
    }

    [Fact]
    public void 达到阈值应当提醒_并带上全部必填字段()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Users\a\large.bin", 20 * 1024 * 1024));

        Assert.Single(raised);

        var r = raised[0];
        Assert.Equal(@"C:\Users\a\large.bin", r.Path);
        Assert.Equal(20 * 1024 * 1024, r.Size);
        Assert.True(r.SizeIsExact);
        Assert.Equal(4242, r.ProcessId);
        Assert.Equal("myapp.exe", r.ProcessName);
        Assert.False(string.IsNullOrEmpty(r.TriggerMode));   // 触发模式
        Assert.False(string.IsNullOrEmpty(r.ThresholdScope));
        Assert.False(string.IsNullOrEmpty(r.CollectorMode));
        Assert.True(r.Notified);
        Assert.NotEqual(default, r.Timestamp);                // 时间
    }

    [Fact]
    public void 模式A默认只提醒新建文件()
    {
        var cfg = Config(c => c.TriggerMode = TriggerMode.AnyWrite);

        Assert.Empty(Run(cfg, Evt(@"C:\Users\a\modified.txt", 1024, kind: WriteKind.Modify)));
        Assert.Single(Run(cfg, Evt(@"C:\Users\a\created.txt", 1024, kind: WriteKind.Create)));
    }

    [Fact]
    public void 模式A关闭该选项后修改也提醒()
    {
        var cfg = Config(c =>
        {
            c.TriggerMode = TriggerMode.AnyWrite;
            c.AnyWriteOnlyNewFiles = false;
        });

        Assert.Single(Run(cfg, Evt(@"C:\Users\a\modified.txt", 1024, kind: WriteKind.Modify)));
    }

    [Fact]
    public void 同一文件短时间内只提醒一次()
    {
        var cfg = Config();
        var raised = Run(cfg,
            Evt(@"C:\Users\a\same.bin", 20 * 1024 * 1024),
            Evt(@"C:\Users\a\same.bin", 20 * 1024 * 1024));

        Assert.Single(raised);
    }

    [Fact]
    public void 静默时段内不弹出提醒()
    {
        var cfg = Config(c =>
        {
            c.QuietHours.Enabled = true;
            c.QuietHours.Start = "00:00";
            c.QuietHours.End = "23:59";
        });

        Assert.Empty(Run(cfg, Evt(@"C:\Users\a\large.bin", 20 * 1024 * 1024)));
    }

    [Fact]
    public void 未知路径默认不弹窗()
    {
        var cfg = Config();
        var e = new FileWriteEvent(
            DateTime.UtcNow.Ticks, null, 20 * 1024 * 1024, 12345, 4242, "myapp.exe",
            true, WriteKind.Modify, CollectorMode.Precise, 1, 0);

        Assert.Empty(Run(cfg, e));
    }

    [Fact]
    public void 未知路径开启选项后可以弹窗()
    {
        var cfg = Config(c => c.NotifyUnknownPath = true);
        var e = new FileWriteEvent(
            DateTime.UtcNow.Ticks, null, 20 * 1024 * 1024, 12345, 4242, "myapp.exe",
            true, WriteKind.Modify, CollectorMode.Precise, 1, 0);

        Assert.Single(Run(cfg, e));
    }

    [Fact]
    public void 标准模式的估算值应当被标注()
    {
        var cfg = Config();
        var raised = Run(cfg, Evt(@"C:\Users\a\est.bin", 20 * 1024 * 1024,
            pid: 0, process: null, source: CollectorMode.Standard));

        Assert.Single(raised);
        Assert.False(raised[0].SizeIsExact);
        Assert.Contains("估算", raised[0].Note);
    }

    [Fact]
    public void 管道应当统计各类排除数量()
    {
        var cfg = Config();
        using var p = new EventPipeline(cfg, selfProcessId: 999_999);
        p.Start();

        p.Post(Evt(@"C:\Users\a\x.bin", 20 * 1024 * 1024, pid: 999_999));          // 自身
        p.Post(Evt(@"C:\Users\a\x.bin", 20 * 1024 * 1024, process: "MsMpEng.exe")); // 进程
        p.Post(Evt(@"C:\Windows\Temp\x.bin", 20 * 1024 * 1024));                    // 路径
        p.Post(Evt(@"C:\Users\a\ok.bin", 20 * 1024 * 1024));                        // 通过

        // 等待「消费完成」且「提醒已发出」——只等 Received 会有竞态：
        // Received 在出队时自增，Notified 在其后才自增。
        var deadline = Environment.TickCount64 + 5000;
        while ((p.Stats.Received < 4 || p.Stats.Notified < 1) && Environment.TickCount64 < deadline) Thread.Sleep(10);

        Assert.Equal(1, p.Stats.ExcludedBySelf);
        Assert.Equal(1, p.Stats.ExcludedByProcess);
        Assert.Equal(1, p.Stats.ExcludedByPath);
        Assert.Equal(1, p.Stats.Notified);
    }

    [Fact]
    public void 暂停时不应处理事件()
    {
        var cfg = Config();
        var raised = new List<AlertRecord>();
        using var p = new EventPipeline(cfg, selfProcessId: 999_999);
        p.AlertRaised += r => raised.Add(r);
        p.Start();

        p.Pause();
        p.Post(Evt(@"C:\Users\a\large.bin", 20 * 1024 * 1024));

        var deadline = Environment.TickCount64 + 1000;
        while (p.Stats.Received < 1 && Environment.TickCount64 < deadline) Thread.Sleep(10);
        Thread.Sleep(50);

        Assert.Empty(raised);

        p.Resume();
        p.Post(Evt(@"C:\Users\a\large2.bin", 20 * 1024 * 1024));

        deadline = Environment.TickCount64 + 2000;
        while (raised.Count == 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);

        Assert.Single(raised);
    }
    // ---------------------------------------------------------------- 采集侧丢失事件

    /// <summary>
    /// ★ 采集器报告的"丢失事件"必须能穿过整条统计链路到达快照。
    /// </summary>
    /// <remarks>
    /// 这条链路的坑在于它**跨了三个文件**（采集器 → CollectorStatsProvider → 快照），
    /// 中间任何一段漏接，界面上就永远是 0 —— 而"永远是 0"看起来和"没有丢失"一模一样，
    /// 是典型的静默失败。所以这里用非零值反向验证接线是通的。
    /// </remarks>
    [Fact]
    public void 采集器报告的丢失事件必须出现在快照里()
    {
        using var p = new EventPipeline(Config(), selfProcessId: 999_999);

        p.CollectorStatsProvider = () => new CollectorStats(
            MonitoredDirectories: 7,
            HandleCount: 42,
            PathMapHits: 0,
            PathMapMisses: 0,
            LostEvents: 123,
            BufferBytes: 0,
            TruncatedByLimit: false);

        var snap = p.Snapshot(cpuPercent: 0, memoryMb: 0);

        Assert.Equal(123, snap.LostEvents);
        Assert.Equal(7, snap.MonitoredDirectories);
        Assert.Equal(42, snap.HandleCount);
    }

    [Fact]
    public void 没有采集器提供者时丢失事件必须为零()
    {
        using var p = new EventPipeline(Config(), selfProcessId: 999_999);

        Assert.Equal(0, p.Snapshot(cpuPercent: 0, memoryMb: 0).LostEvents);
    }

}

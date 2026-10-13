using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
using Xunit;

namespace CWriteWatcher.Tests;

public class ThresholdEvaluatorTests
{
    private const long TenMb = 10L * 1024 * 1024;

    private static FileWriteEvent Evt(long size, long windowKey = 1, int pid = 100, long offset = 0, WriteKind kind = WriteKind.Modify)
        => new(
            timestampTicks: DateTime.UtcNow.Ticks,
            path: @"C:\test\file.bin",
            size: size,
            windowKey: windowKey,
            processId: pid,
            processName: "test.exe",
            sizeIsExact: true,
            kind: kind,
            source: CollectorMode.Precise,
            threadId: 1,
            offset: offset);

    [Fact]
    public void P2_同一文件窗口累计达到阈值应当触发()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);

        // 每次 3 MB，累计到 12 MB 时触发
        Assert.False(ev.Evaluate(Evt(3 * 1024 * 1024), 1000, 3 * 1024 * 1024).Triggered);
        Assert.False(ev.Evaluate(Evt(3 * 1024 * 1024), 1100, 3 * 1024 * 1024).Triggered);

        var r = ev.Evaluate(Evt(3 * 1024 * 1024), 1200, 3 * 1024 * 1024);
        Assert.False(r.Triggered); // 9 MB

        var r2 = ev.Evaluate(Evt(3 * 1024 * 1024), 1300, 3 * 1024 * 1024);
        Assert.True(r2.Triggered); // 12 MB
        Assert.Equal(12 * 1024 * 1024, r2.Accumulated);
    }

    [Fact]
    public void P2_窗口过期后应当重新计数()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);

        Assert.False(ev.Evaluate(Evt(6 * 1024 * 1024), 1000, 6 * 1024 * 1024).Triggered);
        // 超过 1 秒窗口 → 重新开始
        Assert.False(ev.Evaluate(Evt(6 * 1024 * 1024), 2500, 6 * 1024 * 1024).Triggered);
    }

    [Fact]
    public void P2_不同文件应当各自独立计数()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);

        Assert.False(ev.Evaluate(Evt(6 * 1024 * 1024, windowKey: 1), 1000, 6 * 1024 * 1024).Triggered);
        Assert.False(ev.Evaluate(Evt(6 * 1024 * 1024, windowKey: 2), 1000, 6 * 1024 * 1024).Triggered);
    }

    [Fact]
    public void P2_Offset归并后的逻辑写入量应当作为主判据()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);

        // 单次 IRP 只有 1 MB，但 Offset 归并后逻辑写入量已达 100 MB
        var r = ev.Evaluate(Evt(1024 * 1024), 1000, coalescedLogicalSize: 100 * 1024 * 1024);

        Assert.True(r.Triggered);
        Assert.True(r.TriggeredByLogicalWrite);
        Assert.Equal(100 * 1024 * 1024, r.Accumulated);
    }

    [Fact]
    public void P1_单次IRP口径_大文件被拆散时不应触发()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SingleIrp, 1000, 5000, false);

        // 100 个 1 MB 的 IRP：按单次 IRP 判定永远不触发（这正是要避免的口径）
        for (var i = 0; i < 100; i++)
        {
            var r = ev.Evaluate(Evt(1024 * 1024, offset: i * 1024 * 1024), i * 10, 1024 * 1024);
            Assert.False(r.Triggered);
        }
    }

    [Fact]
    public void P3_进程突发口径应当按进程聚合()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.ProcessBurst, 1000, 5000, true);

        // 同一进程 3 次 4 MB，不同文件
        Assert.False(ev.Evaluate(Evt(4 * 1024 * 1024, windowKey: 1, pid: 7), 1000, 0).Triggered);
        Assert.False(ev.Evaluate(Evt(4 * 1024 * 1024, windowKey: 2, pid: 7), 1100, 0).Triggered);

        var r = ev.Evaluate(Evt(4 * 1024 * 1024, windowKey: 3, pid: 7), 1200, 0);
        Assert.True(r.Triggered); // 12 MB
    }

    [Fact]
    public void P3_未启用时应当永不触发()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.ProcessBurst, 1000, 5000, processBurstEnabled: false);

        for (var i = 0; i < 100; i++)
        {
            Assert.False(ev.Evaluate(Evt(1024 * 1024, pid: 7), i * 10, 0).Triggered);
        }
    }

    [Fact]
    public void 触发后重置窗口_允许同一文件再次触发()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);

        var e = Evt(12 * 1024 * 1024);
        var r = ev.Evaluate(e, 1000, 12 * 1024 * 1024);
        Assert.True(r.Triggered);

        ev.ResetAfterTrigger(e);

        // 重置后 12 MB 不应再立刻触发（窗口已清空）
        var r2 = ev.Evaluate(Evt(3 * 1024 * 1024), 1100, 3 * 1024 * 1024);
        Assert.False(r2.Triggered);

        // 但累计到 12 MB 应当再次触发
        ev.Evaluate(Evt(3 * 1024 * 1024), 1200, 3 * 1024 * 1024);
        ev.Evaluate(Evt(3 * 1024 * 1024), 1300, 3 * 1024 * 1024);
        var r3 = ev.Evaluate(Evt(3 * 1024 * 1024), 1400, 3 * 1024 * 1024);
        Assert.True(r3.Triggered);
    }

    [Fact]
    public void 恰好等于阈值应当触发()
    {
        var ev = new ThresholdEvaluator(TenMb, ThresholdScope.SameFileWindow, 1000, 5000, false);
        var r = ev.Evaluate(Evt(TenMb), 1000, TenMb);
        Assert.True(r.Triggered);
    }
}

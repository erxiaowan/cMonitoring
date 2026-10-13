using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
using Xunit;

namespace CWriteWatcher.Tests;

public class OffsetCoalescerTests
{
    private const long Key = 0xABCD;

    [Fact]
    public void Offset连续的碎片应当被归并为一次逻辑写入()
    {
        var c = new OffsetCoalescer();

        // 模拟 NTFS 把一次 100 MB 的写入拆成 10 个 10 MB 的 IRP
        long accumulated = 0;
        for (var i = 0; i < 10; i++)
        {
            accumulated = c.Accumulate(Key, threadId: 1, offset: i * 10 * 1024 * 1024,
                size: 10 * 1024 * 1024, nowMs: 1000 + i * 5, gapMs: 200);
        }

        Assert.Equal(100L * 1024 * 1024, accumulated);
        Assert.Equal(9, c.CoalescedCount);
        Assert.Equal(1, c.NewLogicalWriteCount);
    }

    [Fact]
    public void Offset不连续时应当开启新的逻辑写入()
    {
        var c = new OffsetCoalescer();

        var a1 = c.Accumulate(Key, 1, offset: 0, size: 5_000_000, nowMs: 1000, gapMs: 200);
        Assert.Equal(5_000_000, a1);

        // 跳到别处 → 新的一段
        var a2 = c.Accumulate(Key, 1, offset: 50_000_000, size: 3_000_000, nowMs: 1010, gapMs: 200);
        Assert.Equal(3_000_000, a2);
        Assert.Equal(2, c.NewLogicalWriteCount);
    }

    [Fact]
    public void 超过时间间隔应当开启新的逻辑写入()
    {
        var c = new OffsetCoalescer();

        c.Accumulate(Key, 1, offset: 0, size: 1_000_000, nowMs: 1000, gapMs: 200);

        // Offset 虽然连续，但间隔 500 ms > 200 ms
        var a = c.Accumulate(Key, 1, offset: 1_000_000, size: 1_000_000, nowMs: 1500, gapMs: 200);
        Assert.Equal(1_000_000, a);
        Assert.Equal(2, c.NewLogicalWriteCount);
    }

    [Fact]
    public void 不同线程应当独立归并_避免交错写打断归并链()
    {
        var c = new OffsetCoalescer();

        // 线程 1 写 [0, 5MB)
        c.Accumulate(Key, threadId: 1, offset: 0, size: 5_000_000, nowMs: 1000, gapMs: 200);
        // 线程 2 写 [5MB, 10MB)
        c.Accumulate(Key, threadId: 2, offset: 5_000_000, size: 5_000_000, nowMs: 1000, gapMs: 200);
        // 线程 1 继续写 [5MB, 10MB) —— 若不分线程会被误判为不连续
        var a = c.Accumulate(Key, threadId: 1, offset: 5_000_000, size: 5_000_000, nowMs: 1010, gapMs: 200);

        Assert.Equal(10_000_000, a); // 线程 1 的累计
    }

    [Fact]
    public void 不同FileKey应当独立归并()
    {
        var c = new OffsetCoalescer();

        c.Accumulate(0x1111, 1, offset: 0, size: 4_000_000, nowMs: 1000, gapMs: 200);
        var a = c.Accumulate(0x2222, 1, offset: 0, size: 4_000_000, nowMs: 1000, gapMs: 200);

        Assert.Equal(4_000_000, a);
    }

    [Fact]
    public void Forget应当清除该FileKey的所有线程变体()
    {
        var c = new OffsetCoalescer();

        c.Accumulate(Key, 1, 0, 1_000_000, 1000, 200);
        c.Accumulate(Key, 2, 0, 1_000_000, 1000, 200);
        Assert.Equal(2, c.TrackedKeys);

        c.Forget(Key);
        Assert.Equal(0, c.TrackedKeys);

        // Forget 之后同样的 Offset 会开启新的逻辑写入
        var a = c.Accumulate(Key, 1, 1_000_000, 1_000_000, 1010, 200);
        Assert.Equal(1_000_000, a);
    }

    [Fact]
    public void 超过容量上限时应当清空而不是无界增长()
    {
        var c = new OffsetCoalescer(maxEntries: 100, sweepIntervalMs: 10);

        for (var i = 0; i < 500; i++)
        {
            c.Accumulate(i, 1, 0, 1024, i * 100, 200);
        }

        Assert.True(c.TrackedKeys <= 100, $"TrackedKeys = {c.TrackedKeys}，应当不超过 100");
    }
}

public class RateLimiterTests
{
    private static ThrottleConfig Cfg(
        int perPath = 60, int perProcess = 30, double rate = 0.5, int burst = 5) => new()
        {
            PerPathSeconds = perPath,
            PerProcessSeconds = perProcess,
            GlobalTokensPerSecond = rate,
            GlobalBurst = burst,
        };

    [Fact]
    public void 同一文件在窗口内只放行一次()
    {
        var r = new RateLimiter(Cfg());

        Assert.True(r.TryAcquire(@"C:\a.txt", 100, 1000, out _));
        Assert.False(r.TryAcquire(@"C:\a.txt", 100, 2000, out var reason));
        Assert.Equal(SuppressReason.ThrottledByPath, reason);

        // 60 秒后可以再次放行
        Assert.True(r.TryAcquire(@"C:\a.txt", 100, 1000 + 61_000, out _));
    }

    [Fact]
    public void 同一进程在窗口内只放行一次_即使文件不同()
    {
        var r = new RateLimiter(Cfg(perPath: 0));

        Assert.True(r.TryAcquire(@"C:\a.txt", 100, 1000, out _));
        Assert.False(r.TryAcquire(@"C:\b.txt", 100, 2000, out var reason));
        Assert.Equal(SuppressReason.ThrottledByProcess, reason);

        // 另一个进程不受影响
        Assert.True(r.TryAcquire(@"C:\c.txt", 200, 2000, out _));
    }

    [Fact]
    public void 全局令牌桶应当限制爆发()
    {
        var r = new RateLimiter(Cfg(perPath: 0, perProcess: 0, rate: 0.5, burst: 3));

        // 突发容量 3
        Assert.True(r.TryAcquire(@"C:\1.txt", 1, 1000, out _));
        Assert.True(r.TryAcquire(@"C:\2.txt", 2, 1000, out _));
        Assert.True(r.TryAcquire(@"C:\3.txt", 3, 1000, out _));

        // 第 4 条应当被全局速率拦下
        Assert.False(r.TryAcquire(@"C:\4.txt", 4, 1000, out var reason));
        Assert.Equal(SuppressReason.ThrottledByGlobalRate, reason);

        // 2 秒后补充 1 个令牌
        Assert.True(r.TryAcquire(@"C:\5.txt", 5, 3000, out _));
    }

    [Fact]
    public void 关闭文件级节流时应当只看进程级()
    {
        var r = new RateLimiter(Cfg(perPath: 0, perProcess: 0, rate: 100, burst: 100));

        for (var i = 0; i < 50; i++)
        {
            Assert.True(r.TryAcquire($@"C:\f{i}.txt", 999, 1000 + i, out _));
        }
    }
}

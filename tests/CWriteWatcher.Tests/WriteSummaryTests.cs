using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Logging;
using CWriteWatcher.Core.Models;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>
/// 聚合（趋势）查询的回归测试。
/// </summary>
/// <remarks>
/// <para>这组断言要挡住两类容易出错的地方：</para>
/// <list type="number">
///   <item><b>分组用了 UTC 而不是本地时间</b>：<c>Ts</c> 存的是 Unix 毫秒，
///         直接按 UTC 分组会把东八区晚上 8 点之后的写入算到"明天"。
///         这类错误不会报错，只会让用户看到错误的每日数字。</item>
///   <item><b>聚合与分页用了两套筛选条件</b>：一旦两边不一致，
///         列表里的条数与"合计"就对不上 —— 而两个数字单看都"合理"，极难发现。</item>
/// </list>
/// </remarks>
public class WriteSummaryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"cwt-summary-{Guid.NewGuid():N}.db");

    private EventLogStore CreateStartedStore()
    {
        var store = new EventLogStore(new LoggingConfig { AutoTrim = false }, _dbPath);
        store.Start();
        return store;
    }

    private static AlertRecord Record(
        DateTimeOffset ts, long size, bool exact = true, string process = "chrome.exe") => new()
    {
        Timestamp = ts,
        Path = @"C:\Users\me\file.bin",
        Size = size,
        SizeIsExact = exact,
        ProcessId = 4242,
        ProcessName = process,
        TriggerMode = "B · 超过 10 MB",
        ThresholdScope = "同文件窗口",
        CollectorMode = exact ? "精确模式" : "标准模式",
        Notified = true,
        AggregatedCount = 1,
    };

    private static void EnqueueAndWait(EventLogStore store, IEnumerable<AlertRecord> records)
    {
        var list = records.ToList();
        foreach (var r in list) store.Enqueue(r);

        var deadline = Environment.TickCount64 + 30_000;
        while (store.WrittenCount < list.Count && Environment.TickCount64 < deadline) Thread.Sleep(20);

        Assert.Equal(list.Count, store.WrittenCount);
    }

    // ---------------------------------------------------------------- 合计

    [Fact]
    public async Task 合计必须等于范围内所有记录的字节之和()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[]
        {
            Record(DateTimeOffset.Now, 100),
            Record(DateTimeOffset.Now, 200),
            Record(DateTimeOffset.Now, 300),
        });

        var s = await store.SummarizeAsync(new LogQuery());

        Assert.Equal(3, s.Count);
        Assert.Equal(600, s.TotalBytes);
    }

    [Fact]
    public async Task 全部精确时合计必须是精确值()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[] { Record(DateTimeOffset.Now, 100), Record(DateTimeOffset.Now, 200) });

        var s = await store.SummarizeAsync(new LogQuery());

        Assert.True(s.AllExact, "全部是精确值时必须标记为精确");
        Assert.DoesNotContain("≈", s.DisplayTotal);
    }

    /// <summary>
    /// ★ 只要混进一条估算值，整个合计就只能标成"约"。
    /// </summary>
    /// <remarks>
    /// 这是刻意的保守做法：把估算值当精确值报给用户，比不给数字更糟。
    /// 而且"混了一条"这种情况很容易漏 —— 比如用户从标准模式切到精确模式，
    /// 历史数据里既有估算也有精确。
    /// </remarks>
    [Fact]
    public async Task 混入估算值时合计必须标成约()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[]
        {
            Record(DateTimeOffset.Now, 100, exact: true),
            Record(DateTimeOffset.Now, 200, exact: true),
            Record(DateTimeOffset.Now, 300, exact: false),   // ← 只有一条估算
        });

        var s = await store.SummarizeAsync(new LogQuery());

        Assert.False(s.AllExact, "混有估算值时不得标记为精确");
        Assert.Contains("≈", s.DisplayTotal);
        Assert.Equal(600, s.TotalBytes);
    }

    /// <summary>
    /// ★ 合计必须与分页查询用**同一套筛选条件**。
    /// </summary>
    /// <remarks>
    /// 这条防的是「两个 WHERE 构造器各自演化」：聚合单独写一份筛选，
    /// 改动时只改了一处，就会出现「列表 3 条、合计却是 5 条的量」这种对不上的情况。
    /// </remarks>
    [Fact]
    public async Task 筛选条件必须同样作用于合计()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[]
        {
            Record(DateTimeOffset.Now, 100, process: "chrome.exe"),
            Record(DateTimeOffset.Now, 200, process: "chrome.exe"),
            Record(DateTimeOffset.Now, 4000, process: "backup.exe"),
        });

        var q = new LogQuery { ProcessFilter = "chrome" };

        var rows = await store.QueryAsync(q);
        var s = await store.SummarizeAsync(q);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, s.Count);
        Assert.Equal(300, s.TotalBytes);          // 不能把 backup.exe 的 4000 算进来

        // 两边必须一致 —— 这正是共用 BuildWhere 的意义
        Assert.Equal(rows.Sum(r => r.Size), s.TotalBytes);
    }

    [Fact]
    public async Task 空范围必须返回零而不是报错()
    {
        using var store = CreateStartedStore();

        var s = await store.SummarizeAsync(new LogQuery());

        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.TotalBytes);
        Assert.True(s.AllExact, "没有记录时谈不上估算，应视为精确");
    }

    // ---------------------------------------------------------------- 按天分组

    /// <summary>
    /// ★ 必须按**本地日期**分组。
    /// </summary>
    /// <remarks>
    /// <para>构造三个点：本地「今天 00:30」「今天 23:30」「昨天 23:30」。</para>
    /// <para>如果用 UTC 分组（东八区 = UTC+8），"今天 00:30"的 UTC 时刻是"昨天 16:30"，
    /// 会被算到昨天 —— 结果就是今天 1 条、昨天 2 条，而正确答案是今天 2 条、昨天 1 条。</para>
    /// <para>这个错误不会抛异常，只会让用户看到错的每日数字，所以必须钉死。</para>
    /// </remarks>
    [Fact]
    public async Task 按天统计必须按本地日期分组()
    {
        using var store = CreateStartedStore();

        var today = DateTime.Today;
        var at = (DateTime local) => new DateTimeOffset(local);

        EnqueueAndWait(store, new[]
        {
            Record(at(today.AddMinutes(30)), 100),                       // 今天 00:30
            Record(at(today.AddHours(23).AddMinutes(30)), 200),          // 今天 23:30
            Record(at(today.AddDays(-1).AddHours(23).AddMinutes(30)), 400), // 昨天 23:30
        });

        var days = await store.DailyTotalsAsync(new LogQuery());

        Assert.Equal(2, days.Count);

        // 按日期倒序：今天在前
        Assert.Equal(today, days[0].Date);
        Assert.Equal(2, days[0].Count);
        Assert.Equal(300, days[0].TotalBytes);

        Assert.Equal(today.AddDays(-1), days[1].Date);
        Assert.Equal(1, days[1].Count);
        Assert.Equal(400, days[1].TotalBytes);
    }

    /// <summary>
    /// ★ 按天合计之和必须等于范围合计 —— 两个查询的一致性约束。
    /// </summary>
    /// <remarks>
    /// 这两个数字会同时出现在界面上（底部汇总行 + 趋势窗口）。
    /// 一旦不等，用户就会看到"合计 1.2 GB，但每天加起来 1.5 GB"。
    /// </remarks>
    [Fact]
    public async Task 按天合计之和必须等于范围合计()
    {
        using var store = CreateStartedStore();

        var today = DateTime.Today;
        EnqueueAndWait(store, new[]
        {
            Record(new DateTimeOffset(today.AddHours(1)), 111),
            Record(new DateTimeOffset(today.AddHours(5)), 222),
            Record(new DateTimeOffset(today.AddDays(-1).AddHours(3)), 333),
            Record(new DateTimeOffset(today.AddDays(-2).AddHours(9)), 444),
        });

        var q = new LogQuery();
        var summary = await store.SummarizeAsync(q);
        var days = await store.DailyTotalsAsync(q);

        Assert.Equal(summary.TotalBytes, days.Sum(d => d.TotalBytes));
        Assert.Equal(summary.Count, days.Sum(d => d.Count));
    }

    /// <summary>日期筛选必须同时作用于合计与按天统计。</summary>
    [Fact]
    public async Task 时间范围必须同时作用于合计与按天统计()
    {
        using var store = CreateStartedStore();

        var today = DateTime.Today;
        EnqueueAndWait(store, new[]
        {
            Record(new DateTimeOffset(today.AddHours(10)), 100),               // 今天
            Record(new DateTimeOffset(today.AddDays(-5).AddHours(10)), 999),   // 5 天前
        });

        var q = new LogQuery
        {
            From = new DateTimeOffset(today),
            To = new DateTimeOffset(today.AddDays(1).AddTicks(-1)),
        };

        var summary = await store.SummarizeAsync(q);
        var days = await store.DailyTotalsAsync(q);

        Assert.Equal(1, summary.Count);
        Assert.Equal(100, summary.TotalBytes);
        Assert.Single(days);
        Assert.Equal(today, days[0].Date);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* ignore */ }
    }
}

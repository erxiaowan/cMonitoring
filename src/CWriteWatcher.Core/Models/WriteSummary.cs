namespace CWriteWatcher.Core.Models;

/// <summary>
/// 某个筛选范围内的写入合计（用于「今天/本周共写了多少」这类聚合展示）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独用一个类型而不是返回元组</b>：这个值要穿过「查询 → 界面」两层，
/// 元组在调用点会退化成 <c>Item1/Item2</c>，读代码时完全不知道哪个是字节数。</para>
/// </remarks>
public readonly struct WriteSummary
{
    /// <summary>范围内的记录条数（不是原始写入次数 —— 聚合行算一条）。</summary>
    public readonly long Count;

    /// <summary>范围内的写入字节合计。</summary>
    public readonly long TotalBytes;

    /// <summary>
    /// 是否**所有**记录都是精确值。
    /// </summary>
    /// <remarks>
    /// 只要范围内混有一条估算值，整个合计就只能标成"约"。
    /// 这是刻意的保守做法：把估算值当精确值报给用户，比不给数字更糟。
    /// </remarks>
    public readonly bool AllExact;

    public WriteSummary(long Count, long TotalBytes, bool AllExact)
    {
        this.Count = Count;
        this.TotalBytes = TotalBytes;
        this.AllExact = AllExact;
    }

    /// <summary>带精度前缀的合计文本（估算时带 <c>≈</c>）。</summary>
    public string DisplayTotal => FileWriteEvent.FormatSize(TotalBytes, AllExact);

    /// <summary>范围内的记录条数，带千位分隔。</summary>
    public string DisplayCount => Count.ToString("N0");
}

/// <summary>按本地日期分组的一天的写入合计。</summary>
public readonly struct DailyWriteTotal
{
    /// <summary>本地日期（时间部分已归零）。</summary>
    public readonly DateTime Date;

    /// <summary>当天的记录条数。</summary>
    public readonly long Count;

    /// <summary>当天的写入字节合计。</summary>
    public readonly long TotalBytes;

    /// <summary>当天所有记录是否都是精确值。</summary>
    public readonly bool AllExact;

    public DailyWriteTotal(DateTime Date, long Count, long TotalBytes, bool AllExact)
    {
        this.Date = Date;
        this.Count = Count;
        this.TotalBytes = TotalBytes;
        this.AllExact = AllExact;
    }

    public string DisplayTotal => FileWriteEvent.FormatSize(TotalBytes, AllExact);

    public string DisplayDate => Date.ToString("yyyy-MM-dd");

    /// <summary>星期几（中文单字），便于看"周末写得少"这类规律。</summary>
    public string DisplayWeekday => Date.DayOfWeek switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };
}

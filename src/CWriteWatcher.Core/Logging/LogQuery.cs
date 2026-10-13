namespace CWriteWatcher.Core.Logging;

/// <summary>日志查询条件。</summary>
public sealed class LogQuery
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }

    /// <summary>进程名包含（不区分大小写）。</summary>
    public string? ProcessFilter { get; set; }

    /// <summary>路径包含（不区分大小写）。</summary>
    public string? PathFilter { get; set; }

    public long? MinSize { get; set; }

    /// <summary>true = 只看实际弹出的提醒；false = 只看被抑制的；null = 全部。</summary>
    public bool? NotifiedOnly { get; set; }

    public int Limit { get; set; } = 500;

    public int Offset { get; set; }

    public bool IsEmpty =>
        From is null && To is null &&
        string.IsNullOrWhiteSpace(ProcessFilter) &&
        string.IsNullOrWhiteSpace(PathFilter) &&
        MinSize is null && NotifiedOnly is null;
}

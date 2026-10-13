namespace CWriteWatcher.Core.Models;

/// <summary>一条已判定的提醒记录（同时用于通知与日志）。</summary>
public sealed class AlertRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>文件完整路径。空字符串表示未知路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>累计写入字节数。</summary>
    public long Size { get; set; }

    /// <summary>Size 是否为精确值。</summary>
    public bool SizeIsExact { get; set; }

    public int ProcessId { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    /// <summary>触发模式（A / B / C）。</summary>
    public string TriggerMode { get; set; } = string.Empty;

    /// <summary>阈值口径（P1 / P2 / P3）。</summary>
    public string ThresholdScope { get; set; } = string.Empty;

    /// <summary>采集模式（标准 / 精确）。</summary>
    public string CollectorMode { get; set; } = string.Empty;

    /// <summary>是否实际弹出了提醒（静默时段 / 节流 / 降级时会为 false，但仍记日志）。</summary>
    public bool Notified { get; set; }

    /// <summary>聚合了多少条原始事件（聚合提醒时 &gt; 1）。</summary>
    public int AggregatedCount { get; set; } = 1;

    /// <summary>备注：静默时段、降级级别、估算说明等。</summary>
    public string Note { get; set; } = string.Empty;

    public string DisplaySize => FileWriteEvent.FormatSize(Size, SizeIsExact);

    public string DisplayPath => string.IsNullOrEmpty(Path) ? "(未知路径)" : Path;

    public string DisplayProcess => string.IsNullOrEmpty(ProcessName)
        ? (ProcessId > 0 ? $"PID {ProcessId}" : "未知进程")
        : ProcessName;
}

/// <summary>一条被记录但未提醒的事件（静默 / 节流 / 降级丢弃统计）。</summary>
public enum SuppressReason
{
    None = 0,
    QuietHours = 1,
    ThrottledByPath = 2,
    ThrottledByProcess = 3,
    ThrottledByGlobalRate = 4,
    GovernanceDowngrade = 5,
    UnknownPath = 6,
    BelowThreshold = 7,
}

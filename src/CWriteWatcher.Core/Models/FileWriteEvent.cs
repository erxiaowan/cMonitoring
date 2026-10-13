namespace CWriteWatcher.Core.Models;

/// <summary>
/// 归一化后的文件写入事件。两种采集模式（标准 / 精确）产出同一种结构，
/// 下游的排除过滤、阈值判定、节流、通知、日志完全复用。
/// </summary>
/// <remarks>
/// 设计为 readonly struct 以避免在高速事件流中产生 GC 压力。
/// 注意：<see cref="Path"/> 与 <see cref="ProcessName"/> 是引用类型，
/// 采集层应尽量复用字符串实例（路径驻留）。
/// </remarks>
public readonly struct FileWriteEvent
{
    /// <summary>事件时间（UTC Ticks）。</summary>
    public readonly long TimestampTicks;

    /// <summary>规范化后的完整路径；null 表示路径未知（映射缺失）。</summary>
    public readonly string? Path;

    /// <summary>本次写入的字节数。精确模式下为真实值，标准模式下为差分估算值。</summary>
    public readonly long Size;

    /// <summary>
    /// 阈值窗口的键。精确模式用 FileKey 的低 64 位，标准模式用路径的稳定哈希。
    /// 由采集层负责填充。
    /// </summary>
    public readonly long WindowKey;

    public readonly int ProcessId;

    /// <summary>进程名；标准模式下可能为 null（拿不到可靠的发起进程）。</summary>
    public readonly string? ProcessName;

    /// <summary>Size 是否为精确值（true = 来自 ETW 的 Write 事件；false = 差分估算）。</summary>
    public readonly bool SizeIsExact;

    public readonly WriteKind Kind;

    public readonly CollectorMode Source;

    /// <summary>发起写入的线程 ID（精确模式用于 Offset 归并分组）。</summary>
    public readonly int ThreadId;

    /// <summary>写入的文件偏移量（仅精确模式有效）。</summary>
    public readonly long Offset;

    public FileWriteEvent(
        long timestampTicks,
        string? path,
        long size,
        long windowKey,
        int processId,
        string? processName,
        bool sizeIsExact,
        WriteKind kind,
        CollectorMode source,
        int threadId = 0,
        long offset = 0)
    {
        TimestampTicks = timestampTicks;
        Path = path;
        Size = size;
        WindowKey = windowKey;
        ProcessId = processId;
        ProcessName = processName;
        SizeIsExact = sizeIsExact;
        Kind = kind;
        Source = source;
        ThreadId = threadId;
        Offset = offset;
    }

    public bool HasKnownPath => !string.IsNullOrEmpty(Path);

    public string DisplayPath => HasKnownPath ? Path! : "(未知路径)";

    public string DisplayProcess => string.IsNullOrEmpty(ProcessName)
        ? (ProcessId > 0 ? $"PID {ProcessId}" : "未知进程")
        : ProcessName!;

    public DateTime LocalTime => new DateTime(TimestampTicks, DateTimeKind.Utc).ToLocalTime();

    public bool IsNewFile => (Kind & WriteKind.Create) != 0;

    public static string FormatSize(long bytes, bool exact)
    {
        string v;
        if (bytes >= 1024L * 1024 * 1024) v = $"{bytes / (double)(1024L * 1024 * 1024):0.##} GB";
        else if (bytes >= 1024L * 1024) v = $"{bytes / (double)(1024L * 1024):0.##} MB";
        else if (bytes >= 1024) v = $"{bytes / 1024.0:0.##} KB";
        else v = $"{bytes} B";
        return exact ? v : $"≈ {v}";
    }
}

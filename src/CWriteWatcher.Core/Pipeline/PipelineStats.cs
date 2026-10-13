using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>管道统计快照，用于设置页的"覆盖率实时反馈"（v0.2 §5.4）。</summary>
public readonly struct PipelineStatsSnapshot
{
    public readonly long Received;
    public readonly long ExcludedByPath;
    public readonly long ExcludedByProcess;
    public readonly long ExcludedBySelf;
    public readonly long UnknownPath;
    public readonly long BelowThreshold;
    public readonly long Triggered;
    public readonly long Notified;
    public readonly long SuppressedQuietHours;
    public readonly long SuppressedThrottle;
    public readonly long SuppressedGovernance;
    public readonly long DroppedByQueue;
    public readonly int QueueLength;
    public readonly int QueueCapacity;
    public readonly double QueueFillPercent;
    public readonly GovernanceLevel Level;
    public readonly double CpuPercent;
    public readonly double MemoryMb;
    public readonly int MonitoredDirectories;
    public readonly long HandleCount;
    public readonly double PathMapHitRate;
    public readonly double EventsPerSecond;

    /// <summary>
    /// 采集侧丢失的事件数（标准模式 = 缓冲区溢出次数，精确模式 = ETW 丢失条数）。
    /// **非零意味着日志不完整**，界面上必须如实显示。
    /// </summary>
    public readonly long LostEvents;

    public PipelineStatsSnapshot(
        long received, long excludedByPath, long excludedByProcess, long excludedBySelf, long unknownPath,
        long belowThreshold, long triggered, long notified,
        long suppressedQuietHours, long suppressedThrottle, long suppressedGovernance, long droppedByQueue,
        int queueLength, int queueCapacity, double queueFillPercent,
        GovernanceLevel level, double cpuPercent, double memoryMb,
        int monitoredDirectories, long handleCount, double pathMapHitRate, double eventsPerSecond,
        long lostEvents)
    {
        Received = received;
        ExcludedByPath = excludedByPath;
        ExcludedByProcess = excludedByProcess;
        ExcludedBySelf = excludedBySelf;
        UnknownPath = unknownPath;
        BelowThreshold = belowThreshold;
        Triggered = triggered;
        Notified = notified;
        SuppressedQuietHours = suppressedQuietHours;
        SuppressedThrottle = suppressedThrottle;
        SuppressedGovernance = suppressedGovernance;
        DroppedByQueue = droppedByQueue;
        QueueLength = queueLength;
        QueueCapacity = queueCapacity;
        QueueFillPercent = queueFillPercent;
        Level = level;
        CpuPercent = cpuPercent;
        MemoryMb = memoryMb;
        MonitoredDirectories = monitoredDirectories;
        HandleCount = handleCount;
        PathMapHitRate = pathMapHitRate;
        EventsPerSecond = eventsPerSecond;
        LostEvents = lostEvents;
    }
}

/// <summary>无锁计数器集合。</summary>
public sealed class PipelineStats
{
    private long _received;
    private long _excludedByPath;
    private long _excludedByProcess;
    private long _excludedBySelf;
    private long _unknownPath;
    private long _belowThreshold;
    private long _triggered;
    private long _notified;
    private long _suppressedQuiet;
    private long _suppressedThrottle;
    private long _suppressedGovernance;
    private long _droppedByQueue;

    public long Received => Interlocked.Read(ref _received);
    public long ExcludedByPath => Interlocked.Read(ref _excludedByPath);
    public long ExcludedByProcess => Interlocked.Read(ref _excludedByProcess);
    public long ExcludedBySelf => Interlocked.Read(ref _excludedBySelf);
    public long UnknownPath => Interlocked.Read(ref _unknownPath);
    public long BelowThreshold => Interlocked.Read(ref _belowThreshold);
    public long Triggered => Interlocked.Read(ref _triggered);
    public long Notified => Interlocked.Read(ref _notified);
    public long SuppressedQuietHours => Interlocked.Read(ref _suppressedQuiet);
    public long SuppressedThrottle => Interlocked.Read(ref _suppressedThrottle);
    public long SuppressedGovernance => Interlocked.Read(ref _suppressedGovernance);
    public long DroppedByQueue => Interlocked.Read(ref _droppedByQueue);

    public void IncReceived() => Interlocked.Increment(ref _received);
    public void IncExcludedByPath() => Interlocked.Increment(ref _excludedByPath);
    public void IncExcludedByProcess() => Interlocked.Increment(ref _excludedByProcess);
    public void IncExcludedBySelf() => Interlocked.Increment(ref _excludedBySelf);
    public void IncUnknownPath() => Interlocked.Increment(ref _unknownPath);
    public void IncBelowThreshold() => Interlocked.Increment(ref _belowThreshold);
    public void IncTriggered() => Interlocked.Increment(ref _triggered);
    public void IncNotified() => Interlocked.Increment(ref _notified);
    public void IncSuppressedQuietHours() => Interlocked.Increment(ref _suppressedQuiet);
    public void IncSuppressedThrottle() => Interlocked.Increment(ref _suppressedThrottle);
    public void IncSuppressedGovernance() => Interlocked.Increment(ref _suppressedGovernance);
    public void AddDroppedByQueue(long n) => Interlocked.Add(ref _droppedByQueue, n);

    public void Reset()
    {
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _excludedByPath, 0);
        Interlocked.Exchange(ref _excludedByProcess, 0);
        Interlocked.Exchange(ref _excludedBySelf, 0);
        Interlocked.Exchange(ref _unknownPath, 0);
        Interlocked.Exchange(ref _belowThreshold, 0);
        Interlocked.Exchange(ref _triggered, 0);
        Interlocked.Exchange(ref _notified, 0);
        Interlocked.Exchange(ref _suppressedQuiet, 0);
        Interlocked.Exchange(ref _suppressedThrottle, 0);
        Interlocked.Exchange(ref _suppressedGovernance, 0);
        Interlocked.Exchange(ref _droppedByQueue, 0);
    }
}

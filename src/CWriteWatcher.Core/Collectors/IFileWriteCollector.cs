using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Collectors;

/// <summary>采集器运行时统计（用于设置页的"覆盖率实时反馈"，v0.2 §5.4）。</summary>
public readonly struct CollectorStats
{
    public readonly int MonitoredDirectories;
    public readonly long HandleCount;
    public readonly long PathMapHits;
    public readonly long PathMapMisses;

    /// <summary>
    /// 丢失事件的计数。**非零就意味着日志不完整**，必须让用户看到。
    /// </summary>
    /// <remarks>
    /// <para>两种模式的**语义不同**，但"用户需要知道"这一点是一样的：</para>
    /// <list type="bullet">
    ///   <item><b>标准模式</b>：<c>ReadDirectoryChangesW</c> 缓冲区溢出
    ///         （<c>ERROR_NOTIFY_ENUM_DIR</c>）的**次数** —— 每次代表一批变更被丢弃，
    ///         具体丢了几条不可知。</item>
    ///   <item><b>精确模式</b>：ETW 报告的**事件条数**（<c>EventsLost</c> +
    ///         <c>RealTimeBuffersLost</c>）—— 条数是精确的。</item>
    /// </list>
    /// <para>这个字段曾经在标准模式下被计数却**从未显示**，而精确模式连计数都没有 ——
    /// 两种模式下的静默丢失对用户都不可见。这是本工具最该避免的失败类型。</para>
    /// </remarks>
    public readonly long LostEvents;

    public readonly long BufferBytes;
    public readonly bool TruncatedByLimit;

    public CollectorStats(
        int MonitoredDirectories,
        long HandleCount,
        long PathMapHits,
        long PathMapMisses,
        long LostEvents,
        long BufferBytes,
        bool TruncatedByLimit)
    {
        this.MonitoredDirectories = MonitoredDirectories;
        this.HandleCount = HandleCount;
        this.PathMapHits = PathMapHits;
        this.PathMapMisses = PathMapMisses;
        this.LostEvents = LostEvents;
        this.BufferBytes = BufferBytes;
        this.TruncatedByLimit = TruncatedByLimit;
    }

    public double HitRate
    {
        get
        {
            var total = PathMapHits + PathMapMisses;
            return total <= 0 ? 1.0 : PathMapHits * 100.0 / total;
        }
    }

    public double BufferMb => BufferBytes / (1024.0 * 1024.0);
}

/// <summary>文件写入采集器。两种实现：标准模式（RDCW）与精确模式（ETW）。</summary>
public interface IFileWriteCollector : IDisposable
{
    CollectorMode Mode { get; }

    bool IsRunning { get; }

    /// <summary>给 UI 展示的当前状态文本。</summary>
    string StatusText { get; }

    /// <summary>事件回调。由宿主设置为 <c>pipeline.Post</c>。</summary>
    Action<FileWriteEvent>? OnEvent { get; set; }

    /// <summary>枚举进度（0–100，-1 表示不确定）。</summary>
    event Action<int, string>? Progress;

    void Start(AppConfig config);

    void Stop();

    CollectorStats GetStats();
}

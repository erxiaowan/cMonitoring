using System.Threading.Channels;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Filtering;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>
/// 处理管道：有界 Channel + 单消费者线程。
/// </summary>
/// <remarks>
/// 处理顺序（v0.2 §3.4：PID 判定必须在第一行）：
/// <list type="number">
///   <item>自身 PID 排除（成本接近 0，绝不进入后续环节）</item>
///   <item>进程名排除</item>
///   <item>路径排除</item>
///   <item>模式 A 的"仅新建文件"约束</item>
///   <item>阈值判定（含 Offset 归并）</item>
///   <item>治理降级（L2+ 暂停模式 A）</item>
///   <item>静默时段</item>
///   <item>三层节流</item>
///   <item>放行 → 通知</item>
/// </list>
/// </remarks>
public sealed class EventPipeline : IDisposable
{
    private readonly Channel<FileWriteEvent> _channel;
    private readonly int _capacity;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumer;

    private ExclusionMatcher _matcher;
    private readonly ThresholdEvaluator _threshold;
    private readonly RateLimiter _rate;
    private readonly OffsetCoalescer _coalescer = new();
    private readonly PipelineStats _stats = new();

    private AppConfig _config;
    private readonly int _selfPid;

    private long _windowStartMs;
    private long _windowEventCount;
    private double _eventsPerSecond;

    private volatile bool _paused;

    /// <summary>一条提醒通过了全部闸门，需要真正通知用户。</summary>
    public event Action<AlertRecord>? AlertRaised;

    /// <summary>一条提醒被抑制（静默 / 节流 / 降级），仅用于记日志。</summary>
    public event Action<AlertRecord, SuppressReason>? AlertSuppressed;

    public PipelineStats Stats => _stats;
    public LoadGovernor Governor { get; }
    public ThresholdEvaluator Threshold => _threshold;
    public RateLimiter RateLimiter => _rate;
    public OffsetCoalescer Coalescer => _coalescer;
    public int SelfProcessId => _selfPid;
    public int QueueLength => _channel.Reader.Count;
    public int QueueCapacity => _capacity;
    public bool IsPaused => _paused;

    /// <summary>由采集器提供：当前已监控目录数 / 句柄数 / 路径映射命中率。</summary>
    /// <summary>采集器统计提供者。整块传 <see cref="Collectors.CollectorStats"/>，
    /// 这样采集器新增字段（例如 LostEvents）不需要再改签名。</summary>
    public Func<Collectors.CollectorStats>? CollectorStatsProvider { get; set; }

    public EventPipeline(AppConfig config, int? selfProcessId = null)
    {
        _config = config;
        _selfPid = selfProcessId ?? Environment.ProcessId;
        _capacity = Math.Max(256, config.Governance.ChannelCapacity);

        _channel = Channel.CreateBounded<FileWriteEvent>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _matcher = ExclusionMatcher.Build(config, _selfPid);
        _threshold = new ThresholdEvaluator(
            config.EffectiveThresholdBytes,
            config.ThresholdScope,
            config.FileWindowMs,
            config.ProcessBurstWindowMs,
            config.ProcessBurstEnabled);

        _rate = new RateLimiter(config.Throttle);
        Governor = new LoadGovernor(config.Governance);
        _windowStartMs = Environment.TickCount64;
    }

    public void Start()
    {
        _consumer ??= Task.Factory.StartNew(
            ConsumeLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        try { _cts.Cancel(); } catch { /* ignore */ }
        _channel.Writer.TryComplete();
        try { _consumer?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
    }

    /// <summary>暂停处理（托盘"暂停监控"）。事件仍会被采集但不做判定，避免恢复后补发一堆提醒。</summary>
    public void Pause() => _paused = true;

    public void Resume()
    {
        _paused = false;
        _threshold.Reconfigure(_config.EffectiveThresholdBytes, _config.ThresholdScope,
            _config.FileWindowMs, _config.ProcessBurstWindowMs, _config.ProcessBurstEnabled);
        _rate.Reset();
    }

    /// <summary>非阻塞投递。队列满时由 Channel 丢弃最旧的事件（绝不阻塞采集线程）。</summary>
    public void Post(in FileWriteEvent e)
    {
        if (_channel.Reader.Count >= _capacity) _stats.AddDroppedByQueue(1);
        _channel.Writer.TryWrite(e);
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        _matcher = ExclusionMatcher.Build(config, _selfPid);
        _threshold.Reconfigure(
            config.EffectiveThresholdBytes,
            config.ThresholdScope,
            config.FileWindowMs,
            config.ProcessBurstWindowMs,
            config.ProcessBurstEnabled);
        _rate.Reconfigure(config.Throttle);
        _coalescer.Clear();
    }

    public ExclusionMatcher Matcher => _matcher;

    private void ConsumeLoop()
    {
        var reader = _channel.Reader;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) break;

                while (reader.TryRead(out var e))
                {
                    try
                    {
                        Process(in e);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Pipeline] 处理事件异常：{ex}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Pipeline] 消费者线程退出：{ex}");
        }
    }

    private void Process(in FileWriteEvent e)
    {
        _stats.IncReceived();
        CountForRate();

        if (_paused) return;

        var cfg = _config;
        var now = Environment.TickCount64;

        // ---------- 1. 自身 PID（第一行，成本接近 0）----------
        if (cfg.Exclusions.ExcludeSelfProcess && e.ProcessId == _selfPid)
        {
            _stats.IncExcludedBySelf();
            return;
        }

        // ---------- 2. 进程名排除 ----------
        if (_matcher.IsExcludedProcess(e.ProcessName))
        {
            _stats.IncExcludedByProcess();
            return;
        }

        // ---------- 3. 路径排除 ----------
        if (e.HasKnownPath)
        {
            if (_matcher.IsExcludedPath(e.Path.AsSpan()))
            {
                _stats.IncExcludedByPath();
                return;
            }
        }
        else
        {
            _stats.IncUnknownPath();
            // v0.2 §2.3-5：映射缺失时计入统计但不弹窗，避免噪音
            if (!cfg.NotifyUnknownPath) return;
        }

        // ---------- 4. 模式 A 只提醒新建文件（Q13）----------
        if (cfg.TriggerMode == TriggerMode.AnyWrite && cfg.AnyWriteOnlyNewFiles && !e.IsNewFile)
        {
            _stats.IncBelowThreshold();
            return;
        }

        // ---------- 5. 阈值判定 ----------
        var logicalSize = e.Size;
        if (cfg.OffsetCoalescingEnabled && e.Source == CollectorMode.Precise)
        {
            logicalSize = _coalescer.Accumulate(e.WindowKey, e.ThreadId, e.Offset, e.Size, now, cfg.OffsetCoalesceGapMs);
        }

        var result = _threshold.Evaluate(in e, now, logicalSize);

        if (!result.Triggered)
        {
            _stats.IncBelowThreshold();
            if (Governor.ShouldDropNonThreshold) Governor.RecordDrop();
            return;
        }

        _threshold.ResetAfterTrigger(in e);
        _stats.IncTriggered();

        // ---------- 6. 治理降级：L2+ 暂停模式 A ----------
        if (Governor.ShouldSuspendModeA && cfg.TriggerMode == TriggerMode.AnyWrite)
        {
            _stats.IncSuppressedGovernance();
            Governor.RecordDrop();
            Emit(e, result, notified: false, SuppressReason.GovernanceDowngrade);
            return;
        }

        // ---------- 7. 静默时段 ----------
        if (QuietHours.IsQuiet(cfg.QuietHours, DateTime.Now))
        {
            _stats.IncSuppressedQuietHours();
            Emit(e, result, notified: false, SuppressReason.QuietHours);
            return;
        }

        // ---------- 8. 三层节流 ----------
        if (!_rate.TryAcquire(e.Path, e.ProcessId, now, out var reason))
        {
            _stats.IncSuppressedThrottle();
            Emit(e, result, notified: false, reason);
            return;
        }

        // ---------- 9. 放行 ----------
        _stats.IncNotified();
        Emit(e, result, notified: true, SuppressReason.None);
    }

    private void Emit(in FileWriteEvent e, in ThresholdResult result, bool notified, SuppressReason reason)
    {
        var cfg = _config;

        var record = new AlertRecord
        {
            Timestamp = new DateTimeOffset(e.TimestampTicks, TimeSpan.Zero).ToLocalTime(),
            Path = e.HasKnownPath ? e.Path! : string.Empty,
            Size = result.Accumulated > 0 ? result.Accumulated : e.Size,
            SizeIsExact = e.SizeIsExact,
            ProcessId = e.ProcessId,
            ProcessName = e.ProcessName ?? string.Empty,
            TriggerMode = cfg.TriggerMode.ToDisplayName(),
            ThresholdScope = cfg.ThresholdScope.ToDisplayName(),
            CollectorMode = e.Source.ToDisplayName(),
            Notified = notified,
            AggregatedCount = 1,
            Note = BuildNote(e, result, reason, cfg),
        };

        if (notified) AlertRaised?.Invoke(record);
        else AlertSuppressed?.Invoke(record, reason);
    }

    private static string BuildNote(in FileWriteEvent e, in ThresholdResult result, SuppressReason reason, AppConfig cfg)
    {
        var parts = new List<string>(3);

        if (!e.SizeIsExact)
            parts.Add("写入量为估算值（标准模式）");

        if (result.TriggeredByLogicalWrite)
            parts.Add("由 Offset 归并后的逻辑写入量触发");

        switch (reason)
        {
            case SuppressReason.QuietHours:
                parts.Add($"处于静默时段（{QuietHours.Describe(cfg.QuietHours)}），未弹出提醒");
                break;
            case SuppressReason.ThrottledByPath:
                parts.Add($"同一文件 {cfg.Throttle.PerPathSeconds} 秒内已提醒过");
                break;
            case SuppressReason.ThrottledByProcess:
                parts.Add($"同一进程 {cfg.Throttle.PerProcessSeconds} 秒内已提醒过");
                break;
            case SuppressReason.ThrottledByGlobalRate:
                parts.Add("触发全局提醒速率上限");
                break;
            case SuppressReason.GovernanceDowngrade:
                parts.Add("系统负载过高，已临时降级");
                break;
        }

        return string.Join("；", parts);
    }

    private void CountForRate()
    {
        _windowEventCount++;
        var now = Environment.TickCount64;
        var elapsed = now - _windowStartMs;
        if (elapsed < 1000) return;

        _eventsPerSecond = _windowEventCount * 1000.0 / elapsed;
        _windowEventCount = 0;
        _windowStartMs = now;
    }

    /// <summary>采集侧报告的队列压力（供 LoadGovernor 判定 L3）。</summary>
    public double QueueFillPercent =>
        _capacity <= 0 ? 0 : Math.Clamp(_channel.Reader.Count * 100.0 / _capacity, 0, 100);

    public double EventsPerSecond => _eventsPerSecond;

    public PipelineStatsSnapshot Snapshot(double cpuPercent, double memoryMb)
    {
        var cs = CollectorStatsProvider?.Invoke() ?? default;

        return new PipelineStatsSnapshot(
            _stats.Received, _stats.ExcludedByPath, _stats.ExcludedByProcess, _stats.ExcludedBySelf,
            _stats.UnknownPath, _stats.BelowThreshold, _stats.Triggered, _stats.Notified,
            _stats.SuppressedQuietHours, _stats.SuppressedThrottle, _stats.SuppressedGovernance,
            _stats.DroppedByQueue + Governor.DroppedEvents,
            _channel.Reader.Count, _capacity, QueueFillPercent,
            Governor.Level, cpuPercent, memoryMb,
            cs.MonitoredDirectories, cs.HandleCount, cs.HitRate, _eventsPerSecond,
            cs.LostEvents);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}

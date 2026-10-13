using System.Diagnostics;
using CWriteWatcher.Core.Collectors;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Diagnostics;
using CWriteWatcher.Core.Logging;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
using CWriteWatcher.Etw;

namespace CWriteWatcher.App.Services;

/// <summary>
/// 把配置、采集器、处理管道、日志、通知串起来的总装配点。
/// </summary>
/// <remarks>
/// 运行时线程模型：
/// <list type="bullet">
///   <item><b>采集线程</b>：RDCW 的 IO 完成回调 / ETW 的 ProcessTrace 线程 → <c>pipeline.Post()</c>（非阻塞）。</item>
///   <item><b>处理线程</b>：管道单消费者 → 过滤 / 阈值 / 节流 → 触发事件。</item>
///   <item><b>日志线程</b>：SQLite 批量事务写入。</item>
///   <item><b>治理定时器</b>：每 2 秒采样 CPU 与内存，驱动四级降级与恢复。</item>
///   <item><b>聚合定时器</b>：每 250 ms 冲刷过期的聚合桶。</item>
/// </list>
/// </remarks>
public sealed class WatcherHost : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly CpuSampler _cpu = new();
    private readonly NotificationService _notifier;
    private readonly AlertAggregator _aggregator;

    private EventPipeline? _pipeline;
    private EventLogStore? _logStore;
    private IFileWriteCollector? _collector;

    private System.Threading.Timer? _governorTimer;
    private System.Threading.Timer? _aggregatorTimer;
    private System.Threading.Timer? _pauseTimer;

    private long _pauseUntilTick;
    private volatile bool _running;
    private volatile string _collectorStatus = "未启动";

    public ConfigStore Config => _configStore;
    public EventPipeline? Pipeline => _pipeline;
    public EventLogStore? LogStore => _logStore;
    public IFileWriteCollector? Collector => _collector;
    public NotificationService Notifier => _notifier;
    public string CollectorStatus => _collectorStatus;

    public event Action<PipelineStatsSnapshot>? SnapshotUpdated;
    public event Action<AlertRecord>? AlertNotified;

    public bool IsPaused => _pauseUntilTick > 0 && Environment.TickCount64 < _pauseUntilTick;
    public DateTimeOffset? PausedUntil => IsPaused
        ? DateTimeOffset.Now.AddMilliseconds(_pauseUntilTick - Environment.TickCount64)
        : null;

    public WatcherHost(ConfigStore configStore)
    {
        _configStore = configStore;
        _notifier = new NotificationService();
        _aggregator = new AlertAggregator(
            configStore.Current.Notification.AggregateWindowSeconds * 1000,
            configStore.Current.Notification.MaxAggregateItems);
    }

    public void AttachTray(NotifyIcon tray) => _notifier.AttachTray(tray);

    /// <summary>把 UI 线程上下文与「打开日志」动作交给通知服务（自绘置顶窗需要）。</summary>
    public void AttachUi(SynchronizationContext? uiContext, Action openLog) =>
        _notifier.AttachUi(uiContext, openLog);

    // ---------------------------------------------------------------- 生命周期

    public void Start()
    {
        if (_running) return;
        _running = true;

        var cfg = _configStore.Current;

        _logStore = new EventLogStore(cfg.Logging);
        _logStore.Start();

        _pipeline = new EventPipeline(cfg);
        _pipeline.CollectorStatsProvider = GetCollectorStats;
        _pipeline.AlertRaised += OnAlertRaised;
        _pipeline.AlertSuppressed += OnAlertSuppressed;
        _pipeline.Start();

        StartCollector(cfg);

        _governorTimer = new System.Threading.Timer(_ => GovernorTick(), null, 2000, cfg.Governance.CpuSampleIntervalMs);
        _aggregatorTimer = new System.Threading.Timer(_ => FlushAggregator(), null, 250, 250);

        _configStore.Changed += OnConfigChanged;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _configStore.Changed -= OnConfigChanged;

        _governorTimer?.Dispose();
        _governorTimer = null;
        _aggregatorTimer?.Dispose();
        _aggregatorTimer = null;
        _pauseTimer?.Dispose();
        _pauseTimer = null;

        StopCollector();

        if (_pipeline is not null)
        {
            _pipeline.AlertRaised -= OnAlertRaised;
            _pipeline.AlertSuppressed -= OnAlertSuppressed;
            _pipeline.Dispose();
            _pipeline = null;
        }

        _logStore?.Dispose();
        _logStore = null;
    }

    /// <summary>切换采集模式或范围后重启采集器（不需要重启进程）。</summary>
    public void RestartCollector()
    {
        if (!_running) return;
        StopCollector();
        StartCollector(_configStore.Current);
    }

    private void StartCollector(AppConfig cfg)
    {
        _collector = cfg.CollectorMode == CollectorMode.Precise
            ? new EtwKernelFileCollector()
            : new DirectoryWatcherCollector();

        _collector.OnEvent = OnCollectorEvent;
        _collector.Progress += (_, text) => _collectorStatus = text;
        _collector.Start(cfg);
        _collectorStatus = _collector.StatusText;
    }

    private void StopCollector()
    {
        if (_collector is null) return;
        try
        {
            _collector.Progress -= (_, _) => { };
            _collector.OnEvent = null;
            _collector.Stop();
            _collector.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Host] 停止采集器失败：{ex.Message}");
        }
        finally
        {
            _collector = null;
            _collectorStatus = "已停止";
        }
    }

    private void OnCollectorEvent(FileWriteEvent e) => _pipeline?.Post(in e);

    // ---------------------------------------------------------------- 提醒

    private void OnAlertRaised(AlertRecord record)
    {
        var cfg = _configStore.Current;

        if (cfg.Notification.AggregateEnabled)
        {
            _aggregator.Add(record, Environment.TickCount64);
            return;
        }

        Dispatch(record);
    }

    private void OnAlertSuppressed(AlertRecord record, SuppressReason reason)
    {
        _logStore?.Enqueue(record);
    }

    private void FlushAggregator()
    {
        if (!_running) return;
        if (!_configStore.Current.Notification.AggregateEnabled)
        {
            // 关掉聚合时把残留的全部吐出来
            foreach (var r in _aggregator.DrainAll()) Dispatch(r);
            return;
        }

        var now = Environment.TickCount64;
        foreach (var r in _aggregator.DrainExpired(now)) Dispatch(r);
    }

    private void Dispatch(AlertRecord record)
    {
        var cfg = _configStore.Current;
        _notifier.Notify(record, cfg.Notification, cfg.QuietHours, DateTime.Now);
        _logStore?.Enqueue(record);
        AlertNotified?.Invoke(record);
    }

    // ---------------------------------------------------------------- 治理

    private void GovernorTick()
    {
        if (!_running || _pipeline is null) return;

        var cfg = _configStore.Current;
        var cpu = _cpu.SamplePercentOfMachine();
        var mem = _cpu.WorkingSetMb;

        _pipeline.Governor.Update(cpu, _pipeline.QueueFillPercent, Environment.TickCount64);

        // 内存超限 → 激进裁剪日志表，绝不崩溃、绝不拒绝服务（v0.2 §6.5）
        if (cfg.Logging.Enabled && mem > cfg.Governance.MemoryTrimThresholdMb)
        {
            _ = _logStore?.TrimAsync(Math.Max(1000, cfg.Logging.MaxRows / 2));
        }

        SnapshotUpdated?.Invoke(_pipeline.Snapshot(cpu, mem));
    }

    private CollectorStats GetCollectorStats() =>
        _collector?.GetStats() ?? default;

    public PipelineStatsSnapshot GetSnapshot() =>
        _pipeline?.Snapshot(_cpu.SamplePercentOfMachine(), _cpu.WorkingSetMb)
        ?? default;

    // ---------------------------------------------------------------- 暂停 / 恢复

    public void Pause(TimeSpan duration)
    {
        _pauseUntilTick = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        _pipeline?.Pause();

        _pauseTimer?.Dispose();
        _pauseTimer = new System.Threading.Timer(_ =>
        {
            if (!IsPaused) Resume();
        }, null, duration, TimeSpan.FromSeconds(5));
    }

    public void Resume()
    {
        _pauseUntilTick = 0;
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        _pipeline?.Resume();
    }

    // ---------------------------------------------------------------- 配置变更

    private void OnConfigChanged(AppConfig cfg)
    {
        _pipeline?.ApplyConfig(cfg);

        // 采集模式或监控范围变化 → 重启采集器
        var currentMode = _collector?.Mode ?? CollectorMode.Standard;
        if (currentMode != cfg.CollectorMode)
        {
            RestartCollector();
        }
    }

    public void Dispose()
    {
        Stop();
        _notifier.Dispose();
    }
}

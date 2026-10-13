using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>
/// 负载治理：四级自动降级 + **自动恢复**（v0.2 §6.3）。
/// </summary>
/// <remarks>
/// 没有恢复机制的话，一次 Windows Update 全盘扫描就会让工具永久停留在 L2，
/// 之后所有普通写入都不再提醒，而用户完全察觉不到。这是必须补上的一环。
/// </remarks>
public sealed class LoadGovernor
{
    private readonly GovernanceConfig _cfg;

    private GovernanceLevel _level = GovernanceLevel.Normal;
    private long _overLightSinceMs = -1;
    private long _overHeavySinceMs = -1;
    private long _underTargetSinceMs = -1;

    public GovernanceLevel Level => _level;

    public double LastCpuPercent { get; private set; }
    public double LastQueueFillPercent { get; private set; }

    public long DroppedEvents { get; private set; }
    public long DowngradeCount { get; private set; }
    public long RecoveryCount { get; private set; }

    /// <summary>降级级别发生变化时触发 (旧级别, 新级别, 时间戳)。</summary>
    public event Action<GovernanceLevel, GovernanceLevel, long>? LevelChanged;

    public LoadGovernor(GovernanceConfig config) => _cfg = config;

    /// <summary>模式 A 是否应被暂停（L2 及以上）。</summary>
    public bool ShouldSuspendModeA => _cfg.Enabled && _level >= GovernanceLevel.Heavy;

    /// <summary>是否应丢弃未达阈值的事件（L2 及以上）。</summary>
    public bool ShouldDropNonThreshold => _cfg.Enabled && _level >= GovernanceLevel.Heavy;

    /// <summary>是否处于风暴级别（L3）。</summary>
    public bool IsStorm => _cfg.Enabled && _level >= GovernanceLevel.Storm;

    public void Update(double cpuPercent, double queueFillPercent, long nowMs)
    {
        LastCpuPercent = cpuPercent;
        LastQueueFillPercent = queueFillPercent;

        if (!_cfg.Enabled)
        {
            SetLevel(GovernanceLevel.Normal, nowMs);
            return;
        }

        // ---- L3 风暴：队列积压优先于 CPU ----
        if (queueFillPercent >= _cfg.StormQueuePercent)
        {
            SetLevel(GovernanceLevel.Storm, nowMs);
            ResetRecoveryWindow();
            return;
        }

        if (_level == GovernanceLevel.Storm && queueFillPercent >= _cfg.StormQueuePercent * 0.5)
        {
            // 仍有余压，维持 L3
            ResetRecoveryWindow();
            return;
        }

        // ---- 升级判定 ----
        if (cpuPercent >= _cfg.HeavyCpuPercent)
        {
            _overHeavySinceMs = _overHeavySinceMs < 0 ? nowMs : _overHeavySinceMs;
            _underTargetSinceMs = -1;

            if (nowMs - _overHeavySinceMs >= _cfg.HeavyConfirmSeconds * 1000L)
            {
                SetLevel(GovernanceLevel.Heavy, nowMs);
            }
            return;
        }

        _overHeavySinceMs = -1;

        if (cpuPercent >= _cfg.LightCpuPercent && cpuPercent > _cfg.TargetCpuPercent)
        {
            _overLightSinceMs = _overLightSinceMs < 0 ? nowMs : _overLightSinceMs;
            _underTargetSinceMs = -1;

            if (nowMs - _overLightSinceMs >= _cfg.LightConfirmSeconds * 1000L)
            {
                // 不要越过 Heavy 直接跳级
                if (_level < GovernanceLevel.Light) SetLevel(GovernanceLevel.Light, nowMs);
            }
            return;
        }

        _overLightSinceMs = -1;

        // ---- 恢复判定（关键）----
        if (cpuPercent < _cfg.TargetCpuPercent)
        {
            _underTargetSinceMs = _underTargetSinceMs < 0 ? nowMs : _underTargetSinceMs;

            if (nowMs - _underTargetSinceMs >= _cfg.RecoverySeconds * 1000L)
            {
                if (_level > GovernanceLevel.Normal)
                {
                    // 逐级恢复：L3 → L2 → L1 → L0
                    var next = _level - 1;
                    SetLevel(next, nowMs);
                    RecoveryCount++;
                }
                // 重置窗口，让下一级恢复也需要完整的 RecoverySeconds
                _underTargetSinceMs = nowMs;
            }
        }
        else
        {
            _underTargetSinceMs = -1;
        }
    }

    private void ResetRecoveryWindow() => _underTargetSinceMs = -1;

    private void SetLevel(GovernanceLevel next, long nowMs)
    {
        if (next == _level) return;

        var prev = _level;
        _level = next;

        if (next > prev) DowngradeCount++;
        LevelChanged?.Invoke(prev, next, nowMs);
    }

    /// <summary>记录被治理策略丢弃的事件数。</summary>
    public void RecordDrop(long count = 1)
    {
        if (count > 0) DroppedEvents += count;
    }

    /// <summary>生成给 UI 展示的原因说明（v0.2 §6.4：降级必须留痕）。</summary>
    public string DescribeReason()
    {
        if (_level == GovernanceLevel.Normal) return "运行正常。";

        return _level switch
        {
            GovernanceLevel.Light =>
                $"CPU 占用 {LastCpuPercent:0.##}% 高于目标 {_cfg.TargetCpuPercent:0.##}%，已扩大聚合窗口以减少处理开销。",
            GovernanceLevel.Heavy =>
                $"CPU 占用 {LastCpuPercent:0.##}% 持续超过 {_cfg.HeavyCpuPercent:0.##}%，已暂停「模式 A」并只保留阈值触发。",
            GovernanceLevel.Storm =>
                $"事件队列积压达 {LastQueueFillPercent:0.#}%，已丢弃未达阈值的事件以保证系统不被拖慢。",
            _ => string.Empty,
        };
    }
}

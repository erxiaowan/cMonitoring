using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>
/// 三层节流（v0.2 §1.5 / v0.1 §4.5）：文件级 → 进程级 → 全局令牌桶。
/// 与阈值层职责分离：阈值层决定"是否超标"，节流层决定"是否值得打扰用户"。
/// </summary>
public sealed class RateLimiter
{
    private readonly Dictionary<string, long> _lastByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, long> _lastByProcess = new();

    private double _tokens;
    private long _lastRefillMs;

    private long _perPathMs;
    private long _perProcessMs;
    private double _tokensPerSecond;
    private int _burst;
    private readonly int _maxKeys;

    public long SuppressedByPath { get; private set; }
    public long SuppressedByProcess { get; private set; }
    public long SuppressedByGlobal { get; private set; }
    public long Allowed { get; private set; }

    public RateLimiter(ThrottleConfig config)
    {
        _maxKeys = config.MaxTrackedKeys;
        _tokens = config.GlobalBurst;
        _lastRefillMs = 0;
        Reconfigure(config);
    }

    public void Reconfigure(ThrottleConfig config)
    {
        _perPathMs = Math.Max(0, config.PerPathSeconds) * 1000L;
        _perProcessMs = Math.Max(0, config.PerProcessSeconds) * 1000L;
        _tokensPerSecond = Math.Max(0, config.GlobalTokensPerSecond);
        _burst = Math.Max(1, config.GlobalBurst);
        if (_tokens > _burst) _tokens = _burst;
    }

    /// <summary>
    /// 尝试放行一条提醒。被拒绝时通过 <paramref name="reason"/> 给出原因。
    /// </summary>
    public bool TryAcquire(string? path, int processId, long nowMs, out SuppressReason reason)
    {
        reason = SuppressReason.None;

        // 1) 文件级
        if (_perPathMs > 0 && !string.IsNullOrEmpty(path))
        {
            if (_lastByPath.TryGetValue(path, out var last) && nowMs - last < _perPathMs)
            {
                SuppressedByPath++;
                reason = SuppressReason.ThrottledByPath;
                return false;
            }
        }

        // 2) 进程级
        if (_perProcessMs > 0 && processId > 0)
        {
            if (_lastByProcess.TryGetValue(processId, out var last) && nowMs - last < _perProcessMs)
            {
                SuppressedByProcess++;
                reason = SuppressReason.ThrottledByProcess;
                return false;
            }
        }

        // 3) 全局令牌桶
        Refill(nowMs);
        if (_tokens < 1.0)
        {
            SuppressedByGlobal++;
            reason = SuppressReason.ThrottledByGlobalRate;
            return false;
        }
        _tokens -= 1.0;

        // 放行 → 记录时间戳
        if (_perPathMs > 0 && !string.IsNullOrEmpty(path))
        {
            TrimIfNeeded(_lastByPath);
            _lastByPath[path] = nowMs;
        }
        if (_perProcessMs > 0 && processId > 0)
        {
            TrimIfNeeded(_lastByProcess);
            _lastByProcess[processId] = nowMs;
        }

        Allowed++;
        return true;
    }

    private void Refill(long nowMs)
    {
        if (_lastRefillMs == 0)
        {
            _lastRefillMs = nowMs;
            return;
        }

        var elapsedMs = nowMs - _lastRefillMs;
        if (elapsedMs <= 0) return;

        _tokens = Math.Min(_burst, _tokens + elapsedMs / 1000.0 * _tokensPerSecond);
        _lastRefillMs = nowMs;
    }

    private void TrimIfNeeded<TKey>(Dictionary<TKey, long> map) where TKey : notnull
    {
        if (map.Count < _maxKeys) return;

        // 简单粗暴但有效：整体清空。节流表是"尽力而为"的优化，清空只会让下一批多提醒几条，
        // 好过无界增长吃掉内存预算。
        map.Clear();
    }

    public void Reset()
    {
        _lastByPath.Clear();
        _lastByProcess.Clear();
        _tokens = _burst;
        _lastRefillMs = 0;
    }

    public int TrackedPaths => _lastByPath.Count;
    public int TrackedProcesses => _lastByProcess.Count;
}

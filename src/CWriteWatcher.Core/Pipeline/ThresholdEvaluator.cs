using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

public readonly struct ThresholdResult
{
    /// <summary>是否达到阈值。</summary>
    public readonly bool Triggered;

    /// <summary>触发口径下用于展示的累计字节数。</summary>
    public readonly long Accumulated;

    /// <summary>本次是否由 Offset 归并后的逻辑写入量触发（true）还是窗口累计触发（false）。</summary>
    public readonly bool TriggeredByLogicalWrite;

    public ThresholdResult(bool triggered, long accumulated, bool byLogicalWrite)
    {
        Triggered = triggered;
        Accumulated = accumulated;
        TriggeredByLogicalWrite = byLogicalWrite;
    }

    public static readonly ThresholdResult None = new(false, 0, false);
}

/// <summary>
/// 阈值判定（v0.2 §1）。支持 P1 单次 IRP / P2 同文件窗口 / P3 进程突发。
/// </summary>
/// <remarks>
/// 窗口使用**固定窗口（tumbling）**而非真滑动窗口：状态为 (WindowStartMs, Accumulated)，
/// 跨桶即重置。成本 O(1)、零分配。跨桶边界的缺口由 Offset 归并覆盖（v0.2 §1.4）。
/// </remarks>
public sealed class ThresholdEvaluator
{
    private struct Window
    {
        public long StartMs;
        public long Accumulated;
    }

    private readonly Dictionary<long, Window> _fileWindows = new();
    private readonly Dictionary<int, Window> _processWindows = new();

    private readonly int _maxKeys;
    private long _lastSweepMs;

    public long ThresholdBytes { get; private set; }
    public ThresholdScope Scope { get; private set; }
    public int FileWindowMs { get; private set; }
    public int ProcessWindowMs { get; private set; }
    public bool ProcessBurstEnabled { get; private set; }

    public ThresholdEvaluator(
        long thresholdBytes,
        ThresholdScope scope,
        int fileWindowMs,
        int processWindowMs,
        bool processBurstEnabled,
        int maxKeys = 100_000)
    {
        ThresholdBytes = thresholdBytes;
        Scope = scope;
        FileWindowMs = fileWindowMs;
        ProcessWindowMs = processWindowMs;
        ProcessBurstEnabled = processBurstEnabled;
        _maxKeys = maxKeys;
    }

    public void Reconfigure(long thresholdBytes, ThresholdScope scope, int fileWindowMs, int processWindowMs, bool processBurstEnabled)
    {
        ThresholdBytes = thresholdBytes;
        Scope = scope;
        FileWindowMs = fileWindowMs;
        ProcessWindowMs = processWindowMs;
        ProcessBurstEnabled = processBurstEnabled;
        _fileWindows.Clear();
        _processWindows.Clear();
    }

    /// <summary>
    /// 判定一次写入事件。
    /// </summary>
    /// <param name="e">归一化事件。</param>
    /// <param name="nowMs">单调时钟毫秒。</param>
    /// <param name="coalescedLogicalSize">Offset 归并后的逻辑写入量（标准模式传 e.Size）。</param>
    public ThresholdResult Evaluate(in FileWriteEvent e, long nowMs, long coalescedLogicalSize)
    {
        SweepIfNeeded(nowMs);

        switch (Scope)
        {
            case ThresholdScope.SingleIrp:
                return new ThresholdResult(e.Size >= ThresholdBytes, e.Size, false);

            case ThresholdScope.ProcessBurst:
            {
                if (!ProcessBurstEnabled) return ThresholdResult.None;
                var accum = AddToWindow(_processWindows, e.ProcessId, e.Size, nowMs, ProcessWindowMs);
                return new ThresholdResult(accum >= ThresholdBytes, accum, false);
            }

            default: // SameFileWindow
            {
                var accum = AddToWindow(_fileWindows, e.WindowKey, e.Size, nowMs, FileWindowMs);

                // 主判据：Offset 归并后的逻辑写入量；兜底：窗口累计
                if (coalescedLogicalSize >= ThresholdBytes)
                    return new ThresholdResult(true, coalescedLogicalSize, true);

                if (accum >= ThresholdBytes)
                    return new ThresholdResult(true, accum, false);

                return new ThresholdResult(false, accum, false);
            }
        }
    }

    /// <summary>触发后重置对应窗口，让同一文件/进程可以再次触发（v0.2 §1.5：重置与节流分层）。</summary>
    public void ResetAfterTrigger(in FileWriteEvent e)
    {
        if (Scope == ThresholdScope.ProcessBurst)
        {
            _processWindows.Remove(e.ProcessId);
        }
        else if (Scope == ThresholdScope.SameFileWindow)
        {
            _fileWindows.Remove(e.WindowKey);
        }
    }

    private static long AddToWindow<TKey>(Dictionary<TKey, Window> map, TKey key, long size, long nowMs, int windowMs)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var w))
        {
            if (nowMs - w.StartMs >= windowMs)
            {
                w.StartMs = nowMs;
                w.Accumulated = size;
            }
            else
            {
                w.Accumulated += size;
            }
            map[key] = w;
            return w.Accumulated;
        }

        map[key] = new Window { StartMs = nowMs, Accumulated = size };
        return size;
    }

    private void SweepIfNeeded(long nowMs)
    {
        if (nowMs - _lastSweepMs < 5_000 && _fileWindows.Count < _maxKeys && _processWindows.Count < _maxKeys) return;
        _lastSweepMs = nowMs;

        var cutoff = nowMs - 60_000;

        var staleFiles = new List<long>();
        foreach (var kv in _fileWindows)
        {
            if (kv.Value.StartMs < cutoff) staleFiles.Add(kv.Key);
        }
        foreach (var k in staleFiles) _fileWindows.Remove(k);

        var staleProcs = new List<int>();
        foreach (var kv in _processWindows)
        {
            if (kv.Value.StartMs < cutoff) staleProcs.Add(kv.Key);
        }
        foreach (var k in staleProcs) _processWindows.Remove(k);

        if (_fileWindows.Count >= _maxKeys) _fileWindows.Clear();
        if (_processWindows.Count >= _maxKeys) _processWindows.Clear();
    }

    public int TrackedFileWindows => _fileWindows.Count;
    public int TrackedProcessWindows => _processWindows.Count;
}

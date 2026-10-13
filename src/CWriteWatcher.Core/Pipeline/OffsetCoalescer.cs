namespace CWriteWatcher.Core.Pipeline;

/// <summary>
/// Offset 连续性归并（v0.2 §1.3）。把被 NTFS 拆散的 IRP 还原成一次"逻辑写入"。
/// </summary>
/// <remarks>
/// 归并条件（三者同时满足）：
/// <list type="number">
///   <item>相同 FileKey</item>
///   <item>相同 IssuingThreadId（多线程并发写同一文件时 Offset 会交错，必须分组）</item>
///   <item><c>Offset_{n+1} == Offset_n + Size_n</c> 且两次事件间隔 &lt; gapMs</item>
/// </list>
/// 不满足时开启一段新的逻辑写入。返回的 accumulated 即"到目前为止这次逻辑写入的累计字节数"。
/// </remarks>
public sealed class OffsetCoalescer
{
    private struct State
    {
        public long FileKey;
        public long NextOffset;
        public long Accumulated;
        public long LastTickMs;
    }

    private readonly Dictionary<long, State> _states = new();
    private readonly int _maxEntries;
    private readonly int _sweepIntervalMs;
    private long _lastSweepMs;

    public long CoalescedCount { get; private set; }
    public long NewLogicalWriteCount { get; private set; }

    public OffsetCoalescer(int maxEntries = 100_000, int sweepIntervalMs = 5_000)
    {
        _maxEntries = maxEntries;
        _sweepIntervalMs = sweepIntervalMs;
    }

    /// <summary>
    /// 组合键：FileKey 低 64 位与 ThreadId 混成一个 long，避免 ValueTuple 装箱与哈希开销。
    /// </summary>
    private static long Compose(long fileKey, int threadId)
        => unchecked((fileKey * 397) ^ (uint)threadId);

    /// <summary>
    /// 输入一次 Write 事件，返回本次"逻辑写入"的累计字节数。
    /// </summary>
    public long Accumulate(long fileKey, int threadId, long offset, long size, long nowMs, int gapMs)
    {
        SweepIfNeeded(nowMs);

        var key = Compose(fileKey, threadId);

        if (_states.TryGetValue(key, out var st))
        {
            var contiguous = offset == st.NextOffset;
            var inTime = nowMs - st.LastTickMs <= gapMs;

            if (contiguous && inTime)
            {
                st.Accumulated += size;
                st.NextOffset = offset + size;
                st.LastTickMs = nowMs;
                _states[key] = st;
                CoalescedCount++;
                return st.Accumulated;
            }
        }

        // 开启一段新的逻辑写入
        NewLogicalWriteCount++;
        _states[key] = new State
        {
            FileKey = fileKey,
            NextOffset = offset + size,
            Accumulated = size,
            LastTickMs = nowMs,
        };
        return size;
    }

    /// <summary>文件关闭时移除，防止 FileKey 重用污染新文件（v0.2 §2.3）。</summary>
    public void Forget(long fileKey)
    {
        if (_states.Count == 0) return;

        // 同一 FileKey 可能有多个 threadId 变体，逐个匹配移除。
        // Close 事件频率远低于 Write，全表扫描可接受。
        List<long>? toRemove = null;
        foreach (var kv in _states)
        {
            if (kv.Value.FileKey == fileKey)
            {
                (toRemove ??= new List<long>(2)).Add(kv.Key);
            }
        }

        if (toRemove is null) return;
        foreach (var k in toRemove) _states.Remove(k);
    }

    /// <summary>清空（切换配置或重启监控时）。</summary>
    public void Clear()
    {
        _states.Clear();
        CoalescedCount = 0;
        NewLogicalWriteCount = 0;
    }

    public int TrackedKeys => _states.Count;

    private void SweepIfNeeded(long nowMs)
    {
        if (nowMs - _lastSweepMs < _sweepIntervalMs && _states.Count < _maxEntries) return;

        _lastSweepMs = nowMs;

        // 移除超过 5 秒未活动的条目
        var cutoff = nowMs - 5_000;
        var stale = new List<long>();
        foreach (var kv in _states)
        {
            if (kv.Value.LastTickMs < cutoff) stale.Add(kv.Key);
        }
        foreach (var k in stale) _states.Remove(k);

        // 仍然超限则整体清空（宁可丢失归并精度，也不能无界增长）
        if (_states.Count >= _maxEntries) _states.Clear();
    }
}

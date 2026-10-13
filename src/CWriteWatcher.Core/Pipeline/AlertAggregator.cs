using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>
/// 提醒聚合（Q17 默认开启）：把同一进程在短时间窗口内的多条提醒合并为一条，
/// 例如"chrome.exe 5 秒内写入 12 个文件，共 ≈ 340 MB"。
/// </summary>
/// <remarks>
/// 聚合是**展示层**的降噪手段，与节流层独立：
/// 节流决定"这条要不要提醒"，聚合决定"提醒要不要合并成一条"。
/// </remarks>
public sealed class AlertAggregator
{
    private sealed class Bucket
    {
        public int ProcessId;
        public string ProcessName = string.Empty;
        public long FirstMs;
        public long LastMs;
        public long TotalSize;
        public int Count;
        public string LargestPath = string.Empty;
        public long LargestSize;
        public bool AnyExact;
        public bool AnyInexact;
        public string TriggerMode = string.Empty;
        public string ThresholdScope = string.Empty;
        public string CollectorMode = string.Empty;
    }

    private readonly Dictionary<int, Bucket> _buckets = new();
    private readonly int _windowMs;
    private readonly int _maxItems;
    private readonly int _maxBuckets;

    public long AggregatedCount { get; private set; }

    public AlertAggregator(int windowMs, int maxItems, int maxBuckets = 512)
    {
        _windowMs = Math.Max(0, windowMs);
        _maxItems = Math.Max(1, maxItems);
        _maxBuckets = Math.Max(1, maxBuckets);
    }

    public int BucketCount => _buckets.Count;

    /// <summary>加入一条已通过节流的提醒。</summary>
    public void Add(AlertRecord record, long nowMs)
    {
        if (_buckets.Count >= _maxBuckets && !_buckets.ContainsKey(record.ProcessId))
        {
            // 桶太多：先冲刷最旧的
            var oldest = DrainExpired(nowMs, force: true);
            if (oldest.Count > 0) PendingFlush?.Invoke(oldest);
        }

        if (!_buckets.TryGetValue(record.ProcessId, out var b))
        {
            b = new Bucket
            {
                ProcessId = record.ProcessId,
                ProcessName = record.ProcessName,
                FirstMs = nowMs,
                LastMs = nowMs,
                TriggerMode = record.TriggerMode,
                ThresholdScope = record.ThresholdScope,
                CollectorMode = record.CollectorMode,
            };
            _buckets[record.ProcessId] = b;
        }

        b.LastMs = nowMs;
        b.Count++;
        b.TotalSize += record.Size;
        b.AnyExact |= record.SizeIsExact;
        b.AnyInexact |= !record.SizeIsExact;

        // ★ 必须用 b.Count == 1 兜底：b.LargestSize 初值为 0，
        //   若这一桶里所有事件的 Size 都是 0（模式 A 下新建空文件就会产生 size=0 的 ADDED 事件），
        //   则 record.Size > b.LargestSize 恒为 false，LargestPath 永远不被赋值，
        //   最终提醒里会显示"(未知路径)"——而路径其实是知道的。
        if (b.Count == 1 || record.Size > b.LargestSize)
        {
            b.LargestSize = record.Size;
            b.LargestPath = record.Path;
        }

        if (b.Count >= _maxItems)
        {
            var flushed = DrainBucket(record.ProcessId);
            if (flushed is not null) PendingFlush?.Invoke(new List<AlertRecord> { flushed });
        }
    }

    /// <summary>当聚合器需要主动冲刷时触发（例如桶超限）。</summary>
    public event Action<List<AlertRecord>>? PendingFlush;

    /// <summary>取出所有已过窗口的聚合结果。由宿主定时器驱动（建议 250 ms 一次）。</summary>
    public List<AlertRecord> DrainExpired(long nowMs, bool force = false)
    {
        var result = new List<AlertRecord>();
        if (_buckets.Count == 0) return result;

        List<int>? expired = null;
        foreach (var kv in _buckets)
        {
            if (force || nowMs - kv.Value.LastMs >= _windowMs)
            {
                (expired ??= new List<int>()).Add(kv.Key);
            }
        }

        if (expired is null) return result;

        foreach (var pid in expired)
        {
            var r = DrainBucket(pid);
            if (r is not null) result.Add(r);
        }

        return result;
    }

    /// <summary>取出全部（退出或切换配置时）。</summary>
    public List<AlertRecord> DrainAll()
    {
        var result = new List<AlertRecord>(_buckets.Count);
        foreach (var kv in _buckets)
        {
            var r = ToRecord(kv.Value);
            if (r is not null) result.Add(r);
        }
        _buckets.Clear();
        return result;
    }

    private AlertRecord? DrainBucket(int processId)
    {
        if (!_buckets.TryGetValue(processId, out var b)) return null;
        _buckets.Remove(processId);
        return ToRecord(b);
    }

    private AlertRecord? ToRecord(Bucket b)
    {
        if (b.Count == 0) return null;

        if (b.Count == 1)
        {
            return new AlertRecord
            {
                Timestamp = DateTimeOffset.Now,
                Path = b.LargestPath,
                Size = b.TotalSize,
                SizeIsExact = b.AnyExact && !b.AnyInexact,
                ProcessId = b.ProcessId,
                ProcessName = b.ProcessName,
                TriggerMode = b.TriggerMode,
                ThresholdScope = b.ThresholdScope,
                CollectorMode = b.CollectorMode,
                Notified = true,
                AggregatedCount = 1,
                Note = string.Empty,
            };
        }

        AggregatedCount++;

        var pathText = string.IsNullOrEmpty(b.LargestPath)
            ? $"共 {b.Count} 个文件"
            : $"{b.LargestPath} 等 {b.Count} 个文件";

        return new AlertRecord
        {
            Timestamp = DateTimeOffset.Now,
            Path = pathText,
            Size = b.TotalSize,
            SizeIsExact = b.AnyExact && !b.AnyInexact,
            ProcessId = b.ProcessId,
            ProcessName = b.ProcessName,
            TriggerMode = b.TriggerMode,
            ThresholdScope = b.ThresholdScope,
            CollectorMode = b.CollectorMode,
            Notified = true,
            AggregatedCount = b.Count,
            Note = $"聚合提醒：{b.Count} 次写入合并，最大单文件 {FileWriteEvent.FormatSize(b.LargestSize, b.AnyExact)}",
        };
    }
}

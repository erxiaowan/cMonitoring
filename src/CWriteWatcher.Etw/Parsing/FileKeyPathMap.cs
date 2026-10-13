using CWriteWatcher.Core.Util;

namespace CWriteWatcher.Etw.Parsing;

/// <summary>
/// FileKey → 路径 映射表（v0.2 §2）。这是精确模式最大的实现难点。
/// </summary>
/// <remarks>
/// <para><b>生命周期规则：</b></para>
/// <list type="number">
///   <item><c>Create</c> 事件 → 建立映射（含路径规范化）。</item>
///   <item><c>Close</c> 事件 → <b>立即移除</b>，防止 FileKey 被重用后污染新文件。</item>
///   <item>但 <c>Close</c> <b>不保证到达</b>（进程被强杀、关机、驱动持有句柄），
///         因此必须有 TTL + 容量上限双保险。</item>
///   <item>映射缺失 → 返回 null，由上层显示"未知路径"并计入命中率统计。</item>
///   <item><b>排除路径立即释放字符串</b>：判定为排除路径后把 Path 置空，
///         只保留 FileKey → 已排除 的标记。这是把映射表内存从 ~32 MB 压到 &lt;2 MB 的关键。</item>
/// </list>
/// <para><b>已知无法解决的缺口：</b>程序启动之前就已打开的长期句柄（数据库文件、日志文件、
/// 正在下载的大文件）永远不会有 <c>Create</c> 事件，因此会永久显示"未知路径"。
/// 不采用"启动时全盘枚举建立初始映射"的做法——几十万文件需要数秒到数分钟，违反启动性能要求。</para>
/// </remarks>
public sealed class FileKeyPathMap
{
    private struct Entry
    {
        public string? Path;     // null = 未知；"" = 已排除（字符串已释放）
        public long LastTick;
    }

    private readonly Dictionary<ulong, Entry> _map = new(4096);
    private readonly IReadOnlyDictionary<string, string> _deviceMap;
    private readonly int _maxEntries;
    private readonly int _ttlMs;
    private long _sweepTick;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long ExcludedReleases { get; private set; }
    public int Count => _map.Count;

    /// <summary>命中率（0–100）。UI 用它告诉用户精确模式到底有多大效果。</summary>
    public double HitRate
    {
        get
        {
            var total = Hits + Misses;
            return total <= 0 ? 100.0 : Hits * 100.0 / total;
        }
    }

    public FileKeyPathMap(
        IReadOnlyDictionary<string, string>? deviceMap = null,
        int maxEntries = 200_000,
        int ttlMs = 5 * 60 * 1000)
    {
        _deviceMap = deviceMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _maxEntries = maxEntries;
        _ttlMs = ttlMs;
    }

    /// <summary>Create / Rename 事件调用：建立或更新映射。</summary>
    public void Set(ulong fileKey, string? ntPath, long nowTick)
    {
        if (fileKey == 0) return;
        if (string.IsNullOrEmpty(ntPath)) return;

        var path = PathUtil.ConvertDevicePathToDriveLetter(ntPath, _deviceMap);
        if (path.Length == 0) return;

        _map[fileKey] = new Entry { Path = path, LastTick = nowTick };
        SweepIfNeeded(nowTick);
    }

    /// <summary>Close / Delete 事件调用：立即移除，防止 FileKey 重用污染。</summary>
    public void Remove(ulong fileKey)
    {
        if (fileKey == 0) return;
        _map.Remove(fileKey);
    }

    /// <summary>查询路径。返回 false 表示映射缺失。</summary>
    public bool TryGet(ulong fileKey, long nowTick, out string? path)
    {
        if (_map.TryGetValue(fileKey, out var entry))
        {
            entry.LastTick = nowTick;
            _map[fileKey] = entry;
            path = entry.Path;
            Hits++;
            return path is not null;
        }

        path = null;
        Misses++;
        return false;
    }

    /// <summary>
    /// 标记该 FileKey 对应的是排除路径：释放字符串，只留标记。
    /// 这样高频系统文件的路径不会长期占用内存。
    /// </summary>
    public void MarkExcluded(ulong fileKey, long nowTick)
    {
        if (!_map.TryGetValue(fileKey, out var entry)) return;
        if (entry.Path is null or "") return;

        entry.Path = string.Empty;
        entry.LastTick = nowTick;
        _map[fileKey] = entry;
        ExcludedReleases++;
    }

    private void SweepIfNeeded(long nowTick)
    {
        if (nowTick - _sweepTick < 30_000 && _map.Count < _maxEntries) return;
        _sweepTick = nowTick;

        var cutoff = nowTick - _ttlMs;
        var stale = new List<ulong>(256);

        foreach (var kv in _map)
        {
            if (kv.Value.LastTick < cutoff) stale.Add(kv.Key);
            if (stale.Count >= 20_000) break;
        }

        foreach (var k in stale) _map.Remove(k);

        if (stale.Count > 0) Evictions += stale.Count;

        // 仍然超限 → 整体清空。宁可短暂丢失映射精度，也不能无界增长。
        if (_map.Count >= _maxEntries)
        {
            Evictions += _map.Count;
            _map.Clear();
        }
    }

    public void Clear()
    {
        _map.Clear();
        Hits = 0;
        Misses = 0;
    }
}

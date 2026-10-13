using CWriteWatcher.Core.Config;

namespace CWriteWatcher.Core.Pipeline;

/// <summary>静默时段判定。支持跨零点区间（如 23:00 – 07:00）。</summary>
public static class QuietHours
{
    /// <summary>当前时刻是否处于静默时段。</summary>
    public static bool IsQuiet(QuietHoursConfig config, DateTime now) => IsQuiet(config, now.TimeOfDay);

    public static bool IsQuiet(QuietHoursConfig config, TimeSpan now)
    {
        if (config is null || !config.Enabled) return false;
        if (!config.TryGetRange(out var start, out var end)) return false;

        // start == end 视为全天静默
        if (start == end) return true;

        // 不跨零点：23:00 → 07:00 属于跨零点；09:00 → 17:00 属于不跨
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    /// <summary>距离静默时段结束还有多久；不在静默时段返回 TimeSpan.Zero。</summary>
    public static TimeSpan TimeUntilQuietEnds(QuietHoursConfig config, DateTime now)
    {
        if (!IsQuiet(config, now)) return TimeSpan.Zero;
        if (!config.TryGetRange(out _, out var end)) return TimeSpan.Zero;

        var today = now.Date + end;
        return today > now ? today - now : today.AddDays(1) - now;
    }

    /// <summary>用于 UI 展示的可读描述。</summary>
    public static string Describe(QuietHoursConfig config)
    {
        if (config is null || !config.Enabled) return "未启用";
        if (!config.TryGetRange(out var s, out var e)) return "配置无效";
        var wrap = s > e ? "（跨零点）" : string.Empty;
        return $"{s:hh\\:mm} – {e:hh\\:mm}{wrap}";
    }
}

namespace CWriteWatcher.Core.Models;

/// <summary>采集模式。标准模式免管理员但只能估算写入量；精确模式需管理员但可拿到精确字节数。</summary>
public enum CollectorMode
{
    /// <summary>ReadDirectoryChangesW + 文件大小差分估算。普通用户即可运行。</summary>
    Standard = 0,

    /// <summary>ETW Microsoft-Windows-Kernel-File。需要管理员权限。</summary>
    Precise = 1,
}

/// <summary>提醒模式 A / B / C。</summary>
public enum TriggerMode
{
    /// <summary>A：任意新写入就提醒。</summary>
    AnyWrite = 0,

    /// <summary>B：累计写入超过 10 MB 提醒（默认）。</summary>
    Over10MB = 1,

    /// <summary>C：累计写入超过 50 MB 提醒。</summary>
    Over50MB = 2,
}

/// <summary>阈值判定口径（v0.2 §1.2）。</summary>
public enum ThresholdScope
{
    /// <summary>P1：单个 Write 事件的 Size 直接比较。大文件保存几乎不会触发，仅作调试用。</summary>
    SingleIrp = 0,

    /// <summary>P2（默认）：同一文件在时间窗口内累计写入量。</summary>
    SameFileWindow = 1,

    /// <summary>P3：同一进程在时间窗口内累计写入量。语义是"进程突发写入"，不是"单次写入"。</summary>
    ProcessBurst = 2,
}

/// <summary>写入事件的类型。</summary>
[Flags]
public enum WriteKind
{
    None = 0,

    /// <summary>新建文件。</summary>
    Create = 1,

    /// <summary>写入已存在的文件。</summary>
    Modify = 2,

    /// <summary>重命名（精确模式下由 Rename 事件产生）。</summary>
    Rename = 4,

    /// <summary>删除（默认只记日志不提醒）。</summary>
    Delete = 8,
}

/// <summary>负载治理级别（v0.2 §6.3）。</summary>
public enum GovernanceLevel
{
    /// <summary>CPU &lt; 2%，全功能。</summary>
    Normal = 0,

    /// <summary>2% ≤ CPU &lt; 10% 持续 5s：扩大聚合窗口。</summary>
    Light = 1,

    /// <summary>CPU ≥ 10% 持续 10s：暂停模式 A，只保留阈值触发。</summary>
    Heavy = 2,

    /// <summary>Channel 积压 &gt; 80%：DropOldest，只保留超阈值事件。</summary>
    Storm = 3,
}

/// <summary>开机自启的落地位置。</summary>
public enum AutoStartLocation
{
    None = 0,

    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run（标准模式）。</summary>
    RunKey = 1,

    /// <summary>任务计划程序 /RL HIGHEST（精确模式，登录时静默提权启动）。</summary>
    ScheduledTask = 2,
}

public static class TriggerModeExtensions
{
    public const long MB = 1024L * 1024L;

    /// <summary>模式对应的默认阈值（字节）。AnyWrite 返回 0。</summary>
    public static long DefaultThresholdBytes(this TriggerMode mode) => mode switch
    {
        TriggerMode.Over10MB => 10 * MB,
        TriggerMode.Over50MB => 50 * MB,
        _ => 0,
    };

    public static string ToDisplayName(this TriggerMode mode) => mode switch
    {
        TriggerMode.AnyWrite => "A · 任意新写入",
        TriggerMode.Over10MB => "B · 超过 10 MB",
        TriggerMode.Over50MB => "C · 超过 50 MB",
        _ => mode.ToString(),
    };

    /// <summary>设置页里必须原样出现的说明文案（v0.2 §1.7）。</summary>
    public static string ToDescription(this TriggerMode mode, long thresholdBytes, ThresholdScope scope)
    {
        if (mode == TriggerMode.AnyWrite)
            return "任意新文件写入到 C 盘时提醒。";

        var mb = thresholdBytes / (double)MB;
        return scope switch
        {
            ThresholdScope.SingleIrp =>
                $"单个 IRP 写入量超过 {mb:0.##} MB 时提醒。注意：大文件保存通常被拆成多个小 IRP，此口径可能不触发。",
            ThresholdScope.ProcessBurst =>
                $"同一进程在 {5} 秒内累计写入超过 {mb:0.##} MB 时提醒（进程突发，可能是多个小文件）。",
            _ =>
                $"同一文件在 1 秒内累计写入超过 {mb:0.##} MB 时提醒（按逻辑写入量，非单次 IRP）。",
        };
    }
}

public static class ThresholdScopeExtensions
{
    public static string ToDisplayName(this ThresholdScope scope) => scope switch
    {
        ThresholdScope.SingleIrp => "P1 · 单次 IRP",
        ThresholdScope.SameFileWindow => "P2 · 同文件窗口累计（默认）",
        ThresholdScope.ProcessBurst => "P3 · 进程突发累计",
        _ => scope.ToString(),
    };
}

public static class CollectorModeExtensions
{
    public static string ToDisplayName(this CollectorMode mode) => mode switch
    {
        CollectorMode.Standard => "标准模式（免管理员 · 写入量为估算值）",
        CollectorMode.Precise => "精确模式（需管理员 · 写入量精确）",
        _ => mode.ToString(),
    };
}

public static class GovernanceLevelExtensions
{
    public static string ToDisplayName(this GovernanceLevel level) => level switch
    {
        GovernanceLevel.Normal => "L0 正常",
        GovernanceLevel.Light => "L1 轻载",
        GovernanceLevel.Heavy => "L2 重载",
        GovernanceLevel.Storm => "L3 风暴",
        _ => level.ToString(),
    };
}

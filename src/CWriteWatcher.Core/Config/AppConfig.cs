using System.Text.Json;
using System.Text.Json.Serialization;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.Core.Config;

/// <summary>应用配置。整体序列化为 %AppData%\CWriteWatcher\config.json。</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    // ---------- 采集模式 ----------

    /// <summary>标准模式（默认，免管理员）或精确模式（需管理员）。切换需要重启程序。</summary>
    public CollectorMode CollectorMode { get; set; } = CollectorMode.Standard;

    // ---------- 提醒模式 ----------

    /// <summary>Q14 确认：出厂默认为 B（超过 10 MB）。</summary>
    public TriggerMode TriggerMode { get; set; } = TriggerMode.Over10MB;

    /// <summary>阈值判定口径，默认 P2（同文件窗口累计）。</summary>
    public ThresholdScope ThresholdScope { get; set; } = ThresholdScope.SameFileWindow;

    /// <summary>自定义阈值（字节）。为 null 时使用模式默认值（10MB / 50MB）。</summary>
    public long? CustomThresholdBytes { get; set; }

    /// <summary>P2 的窗口长度（毫秒）。</summary>
    public int FileWindowMs { get; set; } = 1000;

    /// <summary>P3 的窗口长度（毫秒）。</summary>
    public int ProcessBurstWindowMs { get; set; } = 5000;

    /// <summary>P3 进程突发告警独立开关，默认关闭（噪音显著高于 P2）。</summary>
    public bool ProcessBurstEnabled { get; set; }

    /// <summary>Q13 确认：模式 A 默认只提醒"新建文件"，不提醒"修改已有文件"。</summary>
    public bool AnyWriteOnlyNewFiles { get; set; } = true;

    /// <summary>Offset 连续性归并（仅精确模式有效）。</summary>
    public bool OffsetCoalescingEnabled { get; set; } = true;

    /// <summary>Offset 归并的最大事件间隔（毫秒）。</summary>
    public int OffsetCoalesceGapMs { get; set; } = 200;

    /// <summary>
    /// 路径未知时是否也提醒。默认关闭（v0.2 §2.3-5）：
    /// 精确模式下程序启动前已打开的长期句柄永远拿不到 Create 事件，
    /// 若对这些"未知路径"弹窗会造成大量无意义提醒。
    /// </summary>
    public bool NotifyUnknownPath { get; set; }

    // ---------- 子配置 ----------

    public MonitorScope Scope { get; set; } = new();
    public ExclusionConfig Exclusions { get; set; } = new();
    public ThrottleConfig Throttle { get; set; } = new();
    public QuietHoursConfig QuietHours { get; set; } = new();
    public NotificationConfig Notification { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public GovernanceConfig Governance { get; set; } = new();

    // ---------- 开机自启 ----------

    public bool AutoStart { get; set; }

    /// <summary>当前自启项实际落地位置（运行时维护，用于"先删后写"清理）。</summary>
    public AutoStartLocation AutoStartLocation { get; set; } = AutoStartLocation.None;

    // ---------- 派生 ----------

    public long EffectiveThresholdBytes => CustomThresholdBytes ?? TriggerMode.DefaultThresholdBytes();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}

/// <summary>监控范围（v0.2 §5）。</summary>
public sealed class MonitorScope
{
    /// <summary>精确模式默认全盘；标准模式需用户显式勾选（会给出句柄爆炸警告）。</summary>
    public bool WholeDriveC { get; set; }

    /// <summary>额外/替代的包含目录（递归）。</summary>
    public List<string> IncludeDirectories { get; set; } = new();

    /// <summary>在包含范围内的目录级排除（区别于文件级排除表）。</summary>
    public List<string> ExcludeDirectories { get; set; } = new();

    /// <summary>标准模式下对系统目录（Program Files / Windows）的浅层覆盖深度。</summary>
    public int ShallowDepthForSystemDirs { get; set; } = 2;

    /// <summary>RDCW 每个目录的通知缓冲区（KB）。目录数多时会被自动下调。</summary>
    public int BufferSizeKb { get; set; } = 64;

    /// <summary>所有缓冲区合计的内存预算（MB），用于自动分配单目录缓冲区大小。</summary>
    public int BufferBudgetMb { get; set; } = 64;

    /// <summary>监控目录数上限，超过则停止展开并提示用户。</summary>
    public int MaxMonitoredDirectories { get; set; } = 20000;
}

/// <summary>排除表配置。</summary>
public sealed class ExclusionConfig
{
    /// <summary>启用内置默认排除表（强烈建议保持开启）。</summary>
    public bool UseBuiltInDefaults { get; set; } = true;

    /// <summary>排除本进程自身的写入（防止"写日志 → 触发提醒 → 再写日志"循环）。</summary>
    public bool ExcludeSelfProcess { get; set; } = true;

    public List<ExclusionRule> Rules { get; set; } = new();

    /// <summary>进程名黑名单（不含路径，仅文件名如 MsMpEng.exe）。</summary>
    public List<string> ExcludedProcesses { get; set; } = new();
}

public sealed class ExclusionRule
{
    public string Pattern { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Note { get; set; } = string.Empty;

    /// <summary>是否为内置规则（内置规则可禁用但不会被用户误删）。</summary>
    public bool IsBuiltIn { get; set; }

    public string Category { get; set; } = "自定义";

    public override string ToString() => Pattern;
}

/// <summary>三层节流配置（v0.2 §1.5）。</summary>
public sealed class ThrottleConfig
{
    /// <summary>同一路径的去重窗口（秒）。0 = 关闭。</summary>
    public int PerPathSeconds { get; set; } = 60;

    /// <summary>同一进程的去重窗口（秒）。0 = 关闭。</summary>
    public int PerProcessSeconds { get; set; } = 30;

    /// <summary>全局令牌桶速率（条/秒）。默认 0.5 = 1 条 / 2 秒。</summary>
    public double GlobalTokensPerSecond { get; set; } = 0.5;

    /// <summary>全局令牌桶突发容量。</summary>
    public int GlobalBurst { get; set; } = 5;

    /// <summary>节流表的容量上限，防止无界增长。</summary>
    public int MaxTrackedKeys { get; set; } = 50000;
}

/// <summary>静默时段配置（支持跨零点）。</summary>
public sealed class QuietHoursConfig
{
    public bool Enabled { get; set; }

    /// <summary>"HH:mm" 格式。</summary>
    public string Start { get; set; } = "23:00";

    public string End { get; set; } = "07:00";

    /// <summary>静默时段内是否也静音提示音。</summary>
    public bool SilenceSound { get; set; } = true;

    public bool TryGetRange(out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        return TimeSpan.TryParse(Start, out start) && TimeSpan.TryParse(End, out end);
    }
}

/// <summary>提醒通道配置。</summary>
public sealed class NotificationConfig
{
    public bool ToastEnabled { get; set; } = true;

    public bool BalloonEnabled { get; set; } = true;

    public bool SoundEnabled { get; set; }

    /// <summary>
    /// 自绘置顶提示窗。**不经过 Windows 通知平台**，因此不受
    /// 「通知总开关」`PushNotifications\ToastEnabled` 与「专注助手 / 勿扰」的影响。
    /// </summary>
    /// <remarks>
    /// 存在的理由：Win10/11 上 Toast 与 <c>NotifyIcon.ShowBalloonTip</c> 走的是**同一条**通知管道，
    /// 系统级一关就两条一起失效，而 API 层面完全不报错（<c>Show()</c> 正常返回）。
    /// 这是本项目实际踩过的坑，所以补一条完全自主的通道。
    /// </remarks>
    public bool PopupEnabled { get; set; } = true;

    /// <summary>置顶提示窗自动关闭的秒数（鼠标悬停时暂停倒计时）。</summary>
    public int PopupSeconds { get; set; } = 8;

    /// <summary>Q17 建议：默认启用聚合（同进程短时间内的多个事件合并为一条）。</summary>
    public bool AggregateEnabled { get; set; } = true;

    public int AggregateWindowSeconds { get; set; } = 5;

    /// <summary>单条聚合提醒最多包含的事件数。</summary>
    public int MaxAggregateItems { get; set; } = 200;
}

/// <summary>日志配置。</summary>
public sealed class LoggingConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Q19 建议：保留 10 万条。</summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>数据库体积上限（MB）。</summary>
    public int MaxSizeMb { get; set; } = 200;

    public bool AutoTrim { get; set; } = true;

    /// <summary>是否把被节流/静默抑制的事件也记入日志。</summary>
    public bool LogSuppressed { get; set; } = true;
}

/// <summary>负载治理配置（v0.2 §6）。</summary>
public sealed class GovernanceConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>CPU 目标值（占整机百分比）。</summary>
    public double TargetCpuPercent { get; set; } = 2.0;

    public double LightCpuPercent { get; set; } = 2.0;

    public double HeavyCpuPercent { get; set; } = 10.0;

    /// <summary>进入 L1 需要持续超标的秒数。</summary>
    public int LightConfirmSeconds { get; set; } = 5;

    /// <summary>进入 L2 需要持续超标的秒数。</summary>
    public int HeavyConfirmSeconds { get; set; } = 10;

    /// <summary>恢复到 L0 需要持续低于目标的秒数（v0.2 §6.3 关键补充）。</summary>
    public int RecoverySeconds { get; set; } = 30;

    /// <summary>Channel 积压超过该百分比进入 L3。</summary>
    public double StormQueuePercent { get; set; } = 80.0;

    public int ChannelCapacity { get; set; } = 20000;

    /// <summary>内存软上限（MB），超过则裁剪日志表。</summary>
    public double MemoryTrimThresholdMb { get; set; } = 150.0;

    public int CpuSampleIntervalMs { get; set; } = 2000;
}

using System.Text.Json;
using CWriteWatcher.Core.Util;

namespace CWriteWatcher.Core.Config;

/// <summary>
/// 配置持久化。原子写入（临时文件 + 替换），避免断电/崩溃导致配置损坏。
/// </summary>
public sealed class ConfigStore
{
    private readonly object _gate = new();
    private AppConfig _current;

    public string FilePath { get; }

    /// <summary>配置被替换后触发（在新值发布之后，且不在锁内回调）。</summary>
    public event Action<AppConfig>? Changed;

    public ConfigStore(string? filePath = null)
    {
        FilePath = filePath ?? PathUtil.ConfigPath;
        _current = LoadFromDisk();
    }

    /// <summary>当前配置。读取是原子的（引用替换）。</summary>
    public AppConfig Current => Volatile.Read(ref _current);

    private AppConfig LoadFromDisk()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.JsonOptions);
                if (cfg is not null)
                {
                    Normalize(cfg);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            TryBackupCorrupted(ex);
        }

        var fresh = new AppConfig();
        Normalize(fresh);
        return fresh;
    }

    private void TryBackupCorrupted(Exception ex)
    {
        try
        {
            var bak = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(FilePath, bak, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] 配置损坏，已备份到 {bak}：{ex.Message}");
        }
        catch
        {
            // 备份失败不影响主流程
        }
    }

    private static void Normalize(AppConfig cfg)
    {
        cfg.Scope ??= new MonitorScope();
        cfg.Exclusions ??= new ExclusionConfig();
        cfg.Throttle ??= new ThrottleConfig();
        cfg.QuietHours ??= new QuietHoursConfig();
        cfg.Notification ??= new NotificationConfig();
        cfg.Logging ??= new LoggingConfig();
        cfg.Governance ??= new GovernanceConfig();

        cfg.Scope.IncludeDirectories ??= new List<string>();
        cfg.Scope.ExcludeDirectories ??= new List<string>();
        cfg.Exclusions.Rules ??= new List<ExclusionRule>();
        cfg.Exclusions.ExcludedProcesses ??= new List<string>();

        SeedBuiltInExclusions(cfg);

        cfg.TriggerMode = Enum.IsDefined(cfg.TriggerMode) ? cfg.TriggerMode : Models.TriggerMode.Over10MB;
        cfg.ThresholdScope = Enum.IsDefined(cfg.ThresholdScope) ? cfg.ThresholdScope : Models.ThresholdScope.SameFileWindow;
        cfg.CollectorMode = Enum.IsDefined(cfg.CollectorMode) ? cfg.CollectorMode : Models.CollectorMode.Standard;

        if (cfg.CustomThresholdBytes is <= 0) cfg.CustomThresholdBytes = null;
        if (cfg.FileWindowMs is < 100 or > 60_000) cfg.FileWindowMs = 1000;
        if (cfg.ProcessBurstWindowMs is < 100 or > 300_000) cfg.ProcessBurstWindowMs = 5000;
        if (cfg.Scope.MaxMonitoredDirectories is < 100 or > 2_000_000) cfg.Scope.MaxMonitoredDirectories = 20000;
        if (cfg.Scope.BufferSizeKb is < 4 or > 1024) cfg.Scope.BufferSizeKb = 64;
        if (cfg.Scope.BufferBudgetMb is < 4 or > 1024) cfg.Scope.BufferBudgetMb = 64;
        if (cfg.Logging.MaxRows is < 1000) cfg.Logging.MaxRows = 100_000;
        if (cfg.Throttle.MaxTrackedKeys is < 1000) cfg.Throttle.MaxTrackedKeys = 50000;

        CapExclusionInput(cfg);
    }

    /// <summary>排除规则的条数与单条长度上限。</summary>
    private const int MaxExclusionRules = 5_000;
    private const int MaxExclusionPatternLength = 500;
    private const int MaxExcludedProcesses = 2_000;

    /// <summary>
    /// 钳制排除表的规模。
    /// </summary>
    /// <remarks>
    /// <para>排除规则是**配置文件可写**的，而匹配跑在<b>每个文件写入事件</b>的热点路径上：
    /// 条数过多会线性拖慢每个事件；单条模式过长会编译出巨大的正则。</para>
    /// <para>超长模式直接<b>丢弃</b>而不是截断 —— 截断会把一条精确规则变成过宽的规则，
    /// 反而扩大排除范围（导致漏报），丢弃更安全。</para>
    /// </remarks>
    private static void CapExclusionInput(AppConfig cfg)
    {
        var rules = cfg.Exclusions!.Rules!;

        if (rules.Count > MaxExclusionRules)
        {
            rules.RemoveRange(MaxExclusionRules, rules.Count - MaxExclusionRules);
        }

        // 只丢弃**超长**模式。空模式虽然也没用，但那是用户自己在界面上留的空行，
        // 在这里悄悄删掉会变成"我没动它却消失了"，不做这种意外变更。
        rules.RemoveAll(static r => r.Pattern is { Length: > MaxExclusionPatternLength });

        var processes = cfg.Exclusions.ExcludedProcesses!;
        if (processes.Count > MaxExcludedProcesses)
        {
            processes.RemoveRange(MaxExcludedProcesses, processes.Count - MaxExcludedProcesses);
        }
    }

    /// <summary>
    /// 把内置默认排除规则播种进配置。
    /// </summary>
    /// <remarks>
    /// 这样做的好处：用户在设置界面里对某条内置规则的"启用/禁用"能被正常持久化，
    /// 而升级后新增的内置规则也会自动补齐（按 Pattern 去重，不影响用户已有的开关状态）。
    /// </remarks>
    private static void SeedBuiltInExclusions(AppConfig cfg)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Exclusions.Rules)
        {
            if (!string.IsNullOrWhiteSpace(r.Pattern)) existing.Add(r.Pattern);
        }

        foreach (var rule in Filtering.DefaultExclusions.BuildPathRules())
        {
            if (existing.Contains(rule.Pattern)) continue;
            cfg.Exclusions.Rules.Add(rule);
            existing.Add(rule.Pattern);
        }

        var existingProcesses = new HashSet<string>(cfg.Exclusions.ExcludedProcesses, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Filtering.DefaultExclusions.BuildProcessNames())
        {
            if (existingProcesses.Contains(name)) continue;
            cfg.Exclusions.ExcludedProcesses.Add(name);
            existingProcesses.Add(name);
        }
    }

    /// <summary>整体替换配置并落盘。</summary>
    public void Save(AppConfig config)
    {
        Normalize(config);
        lock (_gate)
        {
            WriteAtomic(config);
            Volatile.Write(ref _current, config);
        }
        Changed?.Invoke(config);
    }

    /// <summary>在克隆上做修改后保存，避免调用方直接改到共享实例。</summary>
    public AppConfig Update(Action<AppConfig> mutate)
    {
        var clone = Current.Clone();
        mutate(clone);
        Save(clone);
        return clone;
    }

    private void WriteAtomic(AppConfig config)
    {
        PathUtil.EnsureDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(config, AppConfig.JsonOptions);
        var tmp = FilePath + ".tmp";

        // 使用 FileStream + Flush(true) 确保内容真正落盘，再原子替换
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs))
        {
            sw.Write(json);
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>恢复出厂默认（保留自启设置）。</summary>
    public AppConfig ResetToDefaults()
    {
        var autoStart = Current.AutoStart;
        var location = Current.AutoStartLocation;
        var fresh = new AppConfig { AutoStart = autoStart, AutoStartLocation = location };
        Save(fresh);
        return fresh;
    }
}

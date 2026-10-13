using CWriteWatcher.Core.Config;

namespace CWriteWatcher.Core.Filtering;

/// <summary>
/// 内置默认排除表。目的：挡住高频系统写入，避免通知风暴（v0.1 §4.5 + v0.2 §3.3）。
/// 这些规则默认全部启用，用户可逐条禁用。
/// </summary>
public static class DefaultExclusions
{
    public static List<ExclusionRule> BuildPathRules()
    {
        var rules = new List<ExclusionRule>();

        void Add(string category, string pattern, string note)
        {
            rules.Add(new ExclusionRule
            {
                Pattern = pattern,
                Note = note,
                Category = category,
                IsBuiltIn = true,
                Enabled = true,
            });
        }

        // ---------- 1. 系统核心 ----------
        const string c1 = "系统核心";
        Add(c1, @"C:\pagefile.sys", "页面文件");
        Add(c1, @"C:\hiberfil.sys", "休眠文件");
        Add(c1, @"C:\swapfile.sys", "UWP 交换文件");
        Add(c1, @"C:\DumpStack.log.tmp", "崩溃转储辅助");
        Add(c1, @"C:\Windows\Minidump\*", "小内存转储");
        Add(c1, @"C:\Windows\MEMORY.DMP", "完整内存转储");
        Add(c1, @"C:\Windows\Prefetch\*", "预读取缓存");
        Add(c1, @"C:\Windows\Temp\*", "系统临时目录");
        Add(c1, @"C:\Windows\Logs\*", "系统日志");
        Add(c1, @"C:\Windows\System32\LogFiles\*", "IIS/网络日志");
        Add(c1, @"C:\Windows\System32\winevt\Logs\*", "Windows 事件日志（v0.2 新增）");
        Add(c1, @"C:\Windows\SoftwareDistribution\*", "Windows Update 下载缓存");
        Add(c1, @"C:\Windows\WinSxS\*", "组件存储");
        Add(c1, @"C:\Windows\CSC\*", "脱机文件缓存");
        Add(c1, @"C:\Windows\servicing\*", "服务栈");
        Add(c1, @"C:\Windows\assembly\*", "GAC 程序集缓存");
        Add(c1, @"C:\Windows\Installer\*", "MSI 缓存");
        Add(c1, @"C:\Windows\Panther\*", "安装日志");
        Add(c1, @"C:\Windows\SystemTemp\*", "系统临时目录");
        Add(c1, @"C:\$Recycle.Bin\*", "回收站");
        Add(c1, @"C:\$WinREAgent\*", "恢复环境代理");
        Add(c1, @"C:\Recovery\*", "恢复分区数据");
        Add(c1, @"C:\System Volume Information\*", "卷影副本");
        Add(c1, @"C:\bootstat.dat", "启动统计");

        // ---------- 2. 系统服务与遥测 ----------
        const string c2 = "系统服务与遥测";
        Add(c2, @"C:\ProgramData\Microsoft\Windows Defender\*", "Defender 扫描与日志");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\WER\*", "错误报告");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\Caches\*", "系统缓存");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\AppRepository\*", "UWP 应用仓库");
        Add(c2, @"C:\ProgramData\Microsoft\Search\*", "Windows 搜索索引（Windows.edb）");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\SystemData\*", "系统数据");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\ClipSVC\*", "许可服务");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\DeviceMetadataCache\*", "设备元数据缓存");
        Add(c2, @"C:\ProgramData\USOShared\*", "更新编排器");
        Add(c2, @"C:\ProgramData\Microsoft\Windows\DeliveryOptimization\*", "传递优化缓存");
        Add(c2, @"C:\ProgramData\Microsoft\Diagnosis\*", "诊断遥测");
        Add(c2, @"C:\ProgramData\Microsoft\SmsRouter\*", "系统管理");
        Add(c2, @"C:\ProgramData\NVIDIA Corporation\*", "显卡驱动日志");
        Add(c2, @"C:\ProgramData\AMD\*", "显卡驱动日志");
        Add(c2, @"C:\ProgramData\Intel\*", "显卡驱动日志");

        // ---------- 3. 用户级临时与缓存 ----------
        const string c3 = "用户级临时与缓存";
        Add(c3, @"%LOCALAPPDATA%\Temp\*", "用户临时目录");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\INetCache\*", "IE/系统网络缓存");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\WebCache\*", "Web 缓存数据库");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\Explorer\*", "缩略图缓存");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\Notifications\*", "Toast 通知历史（v0.2 新增，防二级循环）");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\SettingSync\*", "设置同步");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\History\*", "历史记录");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\Caches\*", "用户缓存");
        Add(c3, @"%LOCALAPPDATA%\CrashDumps\*", "崩溃转储");
        Add(c3, @"%LOCALAPPDATA%\Packages\*", "UWP 沙箱数据");
        Add(c3, @"%LOCALAPPDATA%\D3DSCache\*", "着色器缓存");
        Add(c3, @"%LOCALAPPDATA%\NVIDIA\DXCache\*", "显卡着色器缓存");
        Add(c3, @"%LOCALAPPDATA%\Microsoft\Windows\Fonts\*", "用户字体缓存");
        Add(c3, @"%APPDATA%\Microsoft\Windows\Recent\*", "最近使用");
        Add(c3, @"%APPDATA%\Microsoft\Windows\Start Menu\*", "开始菜单缓存");
        Add(c3, @"%APPDATA%\Microsoft\Windows\Themes\*", "主题缓存");

        // ---------- 4. 浏览器缓存 ----------
        const string c4 = "浏览器缓存";
        Add(c4, @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache\*", "Chrome 缓存");
        Add(c4, @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Code Cache\*", "Chrome 代码缓存");
        Add(c4, @"%LOCALAPPDATA%\Google\Chrome\User Data\*\GPUCache\*", "Chrome GPU 缓存");
        Add(c4, @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Service Worker\*", "Chrome SW 缓存");
        Add(c4, @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Cache\*", "Edge 缓存");
        Add(c4, @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Code Cache\*", "Edge 代码缓存");
        Add(c4, @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\GPUCache\*", "Edge GPU 缓存");
        Add(c4, @"%LOCALAPPDATA%\BraveSoftware\*\Cache\*", "Brave 缓存");
        Add(c4, @"%APPDATA%\Mozilla\Firefox\Profiles\*\cache2\*", "Firefox 缓存");
        Add(c4, @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2\*", "Firefox 缓存");
        Add(c4, @"%LOCALAPPDATA%\360Chrome\*Cache\*", "360 浏览器缓存");
        Add(c4, @"%LOCALAPPDATA%\Tencent\*\Cache\*", "腾讯系缓存");
        Add(c4, @"%APPDATA%\Tencent\*\Cache\*", "腾讯系缓存");

        // ---------- 5. 同步盘与下载临时 ----------
        const string c5 = "同步盘与下载临时";
        Add(c5, @"%LOCALAPPDATA%\Dropbox\.dropbox.cache\*", "Dropbox 缓存");
        Add(c5, @"%USERPROFILE%\Dropbox\.dropbox.cache\*", "Dropbox 缓存");
        Add(c5, @"%USERPROFILE%\OneDrive\.849C9593-D756-4E56-8D6E-42412F2A707B\*", "OneDrive 缓存");
        Add(c5, @"%LOCALAPPDATA%\Microsoft\OneDrive\logs\*", "OneDrive 日志");
        Add(c5, @"%LOCALAPPDATA%\Microsoft\OneDrive\settings\*", "OneDrive 设置");
        Add(c5, @"*.crdownload", "Chrome 下载临时文件");
        Add(c5, @"*.partial", "下载临时文件");
        Add(c5, @"*.!ut", "uTorrent 临时文件");
        Add(c5, @"*.tmp", "通用临时文件");
        Add(c5, @"*.etl", "ETW 跟踪日志");
        Add(c5, @"*.dmp", "转储文件");

        // ---------- 6. 自身 ----------
        const string c6 = "本程序自身";
        Add(c6, @"%APPDATA%\CWriteWatcher\*", "自身配置与日志数据库（防提醒循环）");
        Add(c6, @"%LOCALAPPDATA%\CWriteWatcher\*", "自身本地缓存");

        return rules;
    }

    /// <summary>默认进程名黑名单（系统高频写入者）。</summary>
    public static List<string> BuildProcessNames() => new()
    {
        // 系统内核/内核代理
        "System",
        "Registry",
        "Memory Compression",
        "Idle",
        "Secure System",
        "csrss.exe",
        "smss.exe",
        "wininit.exe",
        "dwm.exe",
        "fontdrvhost.exe",

        // Defender
        "MsMpEng.exe",
        "NisSrv.exe",
        "MpCmdRun.exe",
        "MpDefenderCoreService.exe",
        "SecurityHealthService.exe",

        // 索引
        "SearchIndexer.exe",
        "SearchProtocolHost.exe",
        "SearchFilterHost.exe",

        // Windows Update / 服务栈
        "TiWorker.exe",
        "TrustedInstaller.exe",
        "MoUsoCoreWorker.exe",
        "UsoClient.exe",
        "wuauclt.exe",
        "DismHost.exe",
        "CompatTelRunner.exe",
        "WmiPrvSE.exe",

        // 事件与遥测
        "svchost.exe",
        "LogonUI.exe",
        "audiodg.exe",

        // 常见第三方安全软件（会大量写日志）
        "360tray.exe",
        "360Safe.exe",
        "ZhuDongFangYu.exe",
        "HipsTray.exe",
        "HipsDaemon.exe",
        "wsctrl.exe",
        "QQPCTray.exe",
        "kxetray.exe",
        "avp.exe",
        "ekrn.exe",

        // 备份/同步
        "OneDrive.exe",
        "Dropbox.exe",
        "FileSyncHelper.exe",
    };
}

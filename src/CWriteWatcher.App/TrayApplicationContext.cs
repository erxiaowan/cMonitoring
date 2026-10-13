using System.Diagnostics;
using CWriteWatcher.App.Forms;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;

namespace CWriteWatcher.App;

/// <summary>托盘常驻上下文。程序没有主窗口，所有交互通过托盘菜单与设置窗体。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WatcherHost _host;
    private readonly SingleInstanceGuard _guard;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseMenu;
    private readonly ToolStripMenuItem _resumeMenu;
    private readonly ToolStripMenuItem _autoStartMenu;
    private readonly ToolStripMenuItem _statusMenu;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private SettingsForm? _settingsForm;
    private LogForm? _logForm;
    private Icon? _currentIcon;
    private DateTime _alertIconUntil = DateTime.MinValue;
    private bool _exiting;

    public TrayApplicationContext(WatcherHost host, SingleInstanceGuard guard)
    {
        _host = host;
        _guard = guard;

        _statusMenu = new ToolStripMenuItem("状态：正在启动…") { Enabled = false };

        _pauseMenu = new ToolStripMenuItem("暂停监控");
        foreach (var minutes in new[] { 15, 30, 60 })
        {
            var m = minutes;
            _pauseMenu.DropDownItems.Add(new ToolStripMenuItem($"{m} 分钟", null, (_, _) => Pause(m)));
        }
        _pauseMenu.DropDownItems.Add(new ToolStripSeparator());
        _pauseMenu.DropDownItems.Add(new ToolStripMenuItem("直到手动恢复", null, (_, _) => Pause(0)));

        _resumeMenu = new ToolStripMenuItem("立即恢复监控", null, (_, _) => Resume()) { Enabled = false };

        _autoStartMenu = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开设置…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("查看日志…", null, (_, _) => ShowLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartMenu);
        menu.Items.Add(new ToolStripMenuItem("导出 ETW 诊断信息…", null, (_, _) => ExportDiagnostics()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开配置目录", null, (_, _) => OpenConfigFolder()));
        menu.Items.Add(new ToolStripMenuItem("关于", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));

        _currentIcon = IconFactory.Normal;
        _tray = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "C 盘写入监控 · 启动中",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _host.AttachTray(_tray);
        // 置顶窗是真实窗体，必须在 UI 线程上创建；提醒却是从聚合定时器线程发出的，
        // 所以把 UI 线程的 SynchronizationContext 交给通知服务，由它 Post 回来。
        _host.AttachUi(SynchronizationContext.Current, ShowLog);
        _host.SnapshotUpdated += OnSnapshot;
        _host.AlertNotified += OnAlertNotified;
        _guard.ActivateRequested += () => Post(ShowSettings);

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => { RefreshMenuState(); MaybeWriteDiagnosticsSnapshot(); };
        _uiTimer.Start();

        _autoStartMenu.Checked = _host.Config.Current.AutoStart;
    }

    private void Post(Action action)
    {
        try
        {
            if (_tray.ContextMenuStrip is { } menu && menu.InvokeRequired)
                menu.BeginInvoke(action);
            else
                action();
        }
        catch { /* 程序正在退出 */ }
    }

    // ---------------------------------------------------------------- 托盘状态

    private void OnSnapshot(PipelineStatsSnapshot snapshot)
    {
        Post(() =>
        {
            var level = snapshot.Level.ToDisplayName();

            // 有丢失事件时在提示里带一个标记 —— 否则用户不打开设置页就永远发现不了
            // "日志其实是残缺的"。不弹窗打扰，但必须看得见。
            var lost = snapshot.LostEvents > 0 ? $" · 丢 {snapshot.LostEvents}" : string.Empty;

            _tray.Text = UiKit.TruncateHard(
                $"C 盘写入监控 · {level} · CPU {snapshot.CpuPercent:0.##}% · 队列 {snapshot.QueueLength}{lost}",
                127);
        });
    }

    private void OnAlertNotified(AlertRecord record)
    {
        Post(() =>
        {
            _alertIconUntil = DateTime.UtcNow.AddSeconds(4);
            ApplyIcon();
        });
    }

    private void RefreshMenuState()
    {
        var paused = _host.IsPaused;
        _resumeMenu.Enabled = paused;

        if (paused)
        {
            var until = _host.PausedUntil;
            _statusMenu.Text = until is null
                ? "状态：已暂停（手动恢复）"
                : $"状态：已暂停至 {until:HH:mm:ss}";
        }
        else
        {
            var snap = _host.GetSnapshot();
            _statusMenu.Text = $"状态：{snap.Level.ToDisplayName()} · 今日已提醒 {snap.Notified} 次";
        }

        _autoStartMenu.Checked = _host.Config.Current.AutoStart;
        ApplyIcon();
    }

    private void ApplyIcon()
    {
        var icon = _host.IsPaused
            ? IconFactory.Paused
            : DateTime.UtcNow < _alertIconUntil ? IconFactory.Alert : IconFactory.Normal;

        if (ReferenceEquals(icon, _currentIcon)) return;
        _currentIcon = icon;
        _tray.Icon = icon;
    }


    // ---------------------------------------------------------------- 菜单动作

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Show();
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_host);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowLog()
    {
        if (_logForm is { IsDisposed: false })
        {
            _logForm.Show();
            _logForm.Activate();
            return;
        }

        _logForm = new LogForm(_host);
        _logForm.FormClosed += (_, _) => _logForm = null;
        _logForm.Show();
    }

    private void Pause(int minutes)
    {
        _host.Pause(minutes <= 0 ? TimeSpan.FromDays(365) : TimeSpan.FromMinutes(minutes));
        RefreshMenuState();
    }

    private void Resume()
    {
        _host.Resume();
        RefreshMenuState();
    }

    private void ToggleAutoStart()
    {
        var cfg = _host.Config.Current;
        var enable = !cfg.AutoStart;

        var location = cfg.CollectorMode == CollectorMode.Precise
            ? AutoStartLocation.ScheduledTask
            : AutoStartLocation.RunKey;

        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        if (!AutoStartManager.Apply(enable, location, exePath, out var error))
        {
            MessageBox.Show(
                $"设置开机自启失败：\n{error}\n\n" +
                (location == AutoStartLocation.ScheduledTask
                    ? "精确模式需要以管理员身份运行才能创建计划任务。"
                    : string.Empty),
                "CWriteWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _host.Config.Update(c =>
        {
            c.AutoStart = enable;
            c.AutoStartLocation = enable ? location : AutoStartLocation.None;
        });

        _autoStartMenu.Checked = enable;
    }

    private void ExportDiagnostics()
    {
        try
        {
            var dir = Core.Util.PathUtil.AppDataDir;
            Core.Util.PathUtil.EnsureDirectory(dir);
            var path = Path.Combine(dir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            File.WriteAllText(path, BuildDiagnosticsText(), System.Text.Encoding.UTF8);

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出诊断信息失败：" + ex.Message, "CWriteWatcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private long _lastSnapshotTick;

    /// <summary>
    /// 每分钟把当前状态写到固定的 <c>diagnostics-latest.txt</c>。
    /// </summary>
    /// <remarks>
    /// 为什么需要：程序若静默消失（崩溃 / 被结束 / 异常退出），时间戳命名的诊断文件永远不会产生，
    /// 事后就完全不知道它当时处于什么状态。固定文件名保证"总有一份最新现场"。
    /// </remarks>
    private void MaybeWriteDiagnosticsSnapshot()
    {
        var now = Environment.TickCount64;
        if (now - _lastSnapshotTick < 60_000) return;
        _lastSnapshotTick = now;

        try
        {
            var dir = Core.Util.PathUtil.AppDataDir;
            Core.Util.PathUtil.EnsureDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "diagnostics-latest.txt"),
                BuildDiagnosticsText(),
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // 快照失败不影响运行
        }
    }

    /// <summary>拼装诊断文本（手动导出与周期快照共用）。</summary>
    private string BuildDiagnosticsText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CWriteWatcher 诊断信息");
        sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("进程：" + Environment.ProcessPath);
        sb.AppendLine("是否管理员：" + (Etw.EtwKernelFileCollector.IsAvailable ? "是" : "否"));
        sb.AppendLine("逻辑核心数：" + Environment.ProcessorCount);
        sb.AppendLine();

        var snap = _host.GetSnapshot();
        sb.AppendLine("=== 运行统计 ===");
        sb.AppendLine($"采集模式：{_host.Config.Current.CollectorMode}");
        sb.AppendLine($"采集器状态：{_host.CollectorStatus}");
        sb.AppendLine($"治理级别：{snap.Level.ToDisplayName()}");
        sb.AppendLine($"CPU：{snap.CpuPercent:0.##}%    内存：{snap.MemoryMb:0.#} MB");
        sb.AppendLine($"接收事件：{snap.Received}");
        sb.AppendLine($"路径排除：{snap.ExcludedByPath}    进程排除：{snap.ExcludedByProcess}    自身排除：{snap.ExcludedBySelf}");
        sb.AppendLine($"未知路径：{snap.UnknownPath}    低于阈值：{snap.BelowThreshold}");
        sb.AppendLine($"触发：{snap.Triggered}    已提醒：{snap.Notified}");
        sb.AppendLine($"静默抑制：{snap.SuppressedQuietHours}    节流抑制：{snap.SuppressedThrottle}    降级抑制：{snap.SuppressedGovernance}");
        sb.AppendLine($"队列：{snap.QueueLength}/{snap.QueueCapacity}（{snap.QueueFillPercent:0.#}%）");
        // 采集侧丢失的事件。非零 = 日志不完整，排查问题时这是第一优先级的信息。
        sb.AppendLine($"丢失事件：{snap.LostEvents}" +
                      (snap.LostEvents > 0
                          ? "   ← 日志不完整（标准模式=缓冲区溢出次数，精确模式=ETW 丢失条数）"
                          : string.Empty));
        sb.AppendLine($"监控目录：{snap.MonitoredDirectories}    句柄：{snap.HandleCount}    路径映射命中率：{snap.PathMapHitRate:0.##}%");
        sb.AppendLine();

        // 提醒通道单独一段：排查「托盘图标变红了但没有弹窗」时，这几行能区分
        // 「程序没发」和「系统把通知吃掉了」。
        var n = _host.Notifier;
        var nc = _host.Config.Current.Notification;
        sb.AppendLine("=== 提醒通道 ===");
        sb.AppendLine($"通道状态：{n.DescribeChannels(nc)}");
        sb.AppendLine($"配置：置顶窗={nc.PopupEnabled}({nc.PopupSeconds}s)  Toast={nc.ToastEnabled}  气泡={nc.BalloonEnabled}  声音={nc.SoundEnabled}  聚合={nc.AggregateEnabled}");
        sb.AppendLine($"已发送：置顶窗 {n.PopupCount} 次 / Toast {n.ToastCount} 次 / 气泡 {n.BalloonCount} 次 / 声音 {n.SoundCount} 次");
        sb.AppendLine($"最近错误：{n.LastError ?? "（无）"}");
        sb.AppendLine("提示：Toast 与托盘气泡走的是**同一条** Windows 通知管道，");
        sb.AppendLine("      只要「通知总开关」(HKCU\\...\\PushNotifications\\ToastEnabled) 为 0，或开着「专注助手」，");
        sb.AppendLine("      两条会一起被静默 —— 且 API 不报错。此时只有「自绘置顶窗」和「声音」还看得见。");
        sb.AppendLine();

        if (_host.Collector is Etw.EtwKernelFileCollector etw)
        {
            sb.AppendLine("=== ETW 事件 Schema（用于核对字段布局）===");
            sb.AppendLine(etw.BuildSchemaReport());
        }

        return sb.ToString();
    }

    private void OpenConfigFolder()
    {
        try
        {
            Core.Util.PathUtil.EnsureDirectory(Core.Util.PathUtil.AppDataDir);
            Process.Start(new ProcessStartInfo(Core.Util.PathUtil.AppDataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开配置目录失败：" + ex.Message, "CWriteWatcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAbout()
    {
        var snap = _host.GetSnapshot();
        MessageBox.Show(
            "CWriteWatcher · C 盘写入监控\n" +
            "版本 0.2.0（.NET 8 / WinForms）\n\n" +
            $"当前采集模式：{_host.Config.Current.CollectorMode.ToDisplayName()}\n" +
            $"是否管理员：{(Etw.EtwKernelFileCollector.IsAvailable ? "是" : "否")}\n\n" +
            "所有数据仅保存在本机，不上传任何内容。\n" +
            $"配置目录：{Core.Util.PathUtil.AppDataDir}",
            "关于 CWriteWatcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        RunLog.Write("用户从托盘菜单选择退出");
        _uiTimer.Stop();

        try
        {
            // 退出时清理"孤儿"自启项：配置里已关闭自启但注册表/计划任务还有残留
            if (!_host.Config.Current.AutoStart) AutoStartManager.RemoveAll();
        }
        catch { /* ignore */ }

        try { _tray.Visible = false; } catch { /* ignore */ }
        try { AlertPopup.CloseAll(); } catch { /* ignore */ }
        try { _host.Stop(); } catch { /* ignore */ }

        RunLog.Write("=== 正常退出 ===");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer?.Dispose();
            _tray?.Dispose();
        }
        base.Dispose(disposing);
    }
}

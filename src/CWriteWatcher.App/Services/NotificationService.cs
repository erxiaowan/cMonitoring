using System.Media;
using CWriteWatcher.App.Forms;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
#if REAL_TOAST
using Microsoft.Toolkit.Uwp.Notifications;
#endif

namespace CWriteWatcher.App.Services;

/// <summary>
/// 提醒通道：Windows Toast + 托盘气泡 + 可选声音（需求 §6）。
/// </summary>
/// <remarks>
/// <para><b>两种构建形态：</b></para>
/// <list type="bullet">
///   <item>默认（<c>UseRealToast=true</c>）：用 <c>ToastNotificationManagerCompat</c> 发真实 Toast，
///         它为非打包桌面应用自动处理 AUMID 注册，无需安装程序。代价是 TFM 必须是
///         <c>net8.0-windows10.0.x</c>，会带上约 24 MB 的 Windows SDK 投影。</item>
///   <item><c>-p:UseRealToast=false</c>：只用 <c>NotifyIcon.ShowBalloonTip</c>。
///         Win10/11 上外壳会把它渲染成系统 Toast 通知，体积降到约 2 MB。</item>
/// </list>
/// <para><b>已知限制：</b>系统开启「专注助手 / 勿扰模式」时通知会被静默，
/// 用户可能收不到提醒。</para>
/// </remarks>
public sealed class NotificationService : IDisposable
{
    private NotifyIcon? _tray;
#if REAL_TOAST
    private bool _toastUnavailable;
    private string? _toastError;
#endif
    private string? _balloonError;
    private string? _popupError;
    private SynchronizationContext? _uiContext;
    private Action? _openLog;

    public long ToastCount { get; private set; }
    public long BalloonCount { get; private set; }
    public long SoundCount { get; private set; }

    /// <summary>自绘置顶提示窗的显示次数。</summary>
    public long PopupCount { get; private set; }

    /// <summary>当前构建是否具备真实 Toast 能力。</summary>
    public static bool SupportsRealToast =>
#if REAL_TOAST
        true;
#else
        false;
#endif

    /// <summary>Toast 是否已被判定为不可用（首次发送抛异常后置位，之后不再重试，改用气泡兜底）。</summary>
    public bool ToastUnavailable =>
#if REAL_TOAST
        _toastUnavailable;
#else
        false;
#endif

    /// <summary>最近一次提醒通道的错误信息（诊断用）。</summary>
    public string? LastError =>
#if REAL_TOAST
        _toastError ?? _balloonError ?? _popupError;
#else
        _balloonError ?? _popupError;
#endif

    /// <summary>
    /// 人类可读的通道状态。用于状态页与诊断导出 —— 排查"托盘图标变红了但没有弹窗"这类问题时，
    /// 这一行能立刻区分「程序没发」和「系统吃掉了」。
    /// </summary>
    public string DescribeChannels(NotificationConfig cfg)
    {
        var parts = new List<string>();

        if (cfg.PopupEnabled) parts.Add("自绘置顶窗");
        if (cfg.SoundEnabled) parts.Add("声音");

        if (cfg.ToastEnabled)
        {
#if REAL_TOAST
            parts.Add(_toastUnavailable ? $"Toast（已降级：{_toastError ?? "未知原因"}）" : "Toast");
#else
            parts.Add("Toast（本构建未编译）");
#endif
        }

        if (cfg.BalloonEnabled) parts.Add("托盘气泡");

        return parts.Count == 0 ? "（全部通道已关闭）" : string.Join(" + ", parts);
    }

    public void AttachTray(NotifyIcon tray) => _tray = tray;

    /// <summary>
    /// 绑定 UI 线程上下文与「打开日志」动作。
    /// </summary>
    /// <remarks>
    /// 提醒是从聚合定时器的线程发出的，而 <see cref="AlertPopup"/> 是真实窗体，
    /// 必须在 UI 线程上创建与显示。捕获 WinForms 的 <see cref="SynchronizationContext"/>
    /// 后用它把显示动作 Post 回 UI 线程。
    /// </remarks>
    public void AttachUi(SynchronizationContext? uiContext, Action openLog)
    {
        _uiContext = uiContext;
        _openLog = openLog;
    }

    /// <summary>
    /// 发送一条**自检提醒**：走与真实提醒完全相同的代码路径，用来验证通道是否可用。
    /// </summary>
    /// <remarks>
    /// 存在的理由：真实提醒要等到监控捕获到符合条件的写入才会触发，
    /// 用户无法快速判断"是通道坏了"还是"还没触发"。自检按钮把这两件事分开。
    /// </remarks>
    public void SendTest(NotificationConfig config, QuietHoursConfig quiet, DateTime now)
    {
        var record = new AlertRecord
        {
            Timestamp = now,
            Path = @"C:\Users\Public\Documents\CWriteWatcher-自检.bin",
            Size = 20L * 1024 * 1024,
            SizeIsExact = true,
            ProcessId = Environment.ProcessId,
            ProcessName = "CWriteWatcher 自检",
            TriggerMode = TriggerMode.Over10MB.ToDisplayName(),
            ThresholdScope = ThresholdScope.SameFileWindow.ToDisplayName(),
            CollectorMode = "自检（非真实事件）",
            Notified = true,
            AggregatedCount = 1,
            Note = "由设置界面「发送测试提醒」按钮触发",
        };

        Notify(record, config, quiet, now);
    }

    public void Notify(AlertRecord record, NotificationConfig config, bool inQuietHours)
    {
        var title = $"C 盘写入提醒 · {record.TriggerMode}";
        var sizeText = record.DisplaySize + (record.SizeIsExact ? "（精确）" : "（估算）");
        var body = $"{record.DisplayProcess} 写入了 {sizeText}";

        // ---------- 通道 1：自绘置顶窗 ----------
        // 放在最前面，因为它是**唯一不受系统通知设置影响**的通道。
        // 系统关掉「通知总开关」或开着「专注助手」时，下面两条都会静默失效，
        // 只有这一条一定看得见。
        if (config.PopupEnabled) ShowPopup(record, config);

#if REAL_TOAST
        if (config.ToastEnabled && !_toastUnavailable)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(body)
                    .AddText(record.DisplayPath)
                    .AddAttributionText($"{record.Timestamp:HH:mm:ss} · {record.CollectorMode}")
                    .Show();
                ToastCount++;
            }
            catch (Exception ex)
            {
                // 常见于系统未注册通知平台；退化为托盘气泡，不再重试 Toast
                _toastUnavailable = true;
                _toastError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[Notify] Toast 不可用，已降级为托盘气泡：{ex.Message}");
            }
        }
#endif

        if (config.BalloonEnabled && _tray is not null)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = $"{body}\n{UiKit.Truncate(record.DisplayPath, 120)}";
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.ShowBalloonTip(8000);
                BalloonCount++;
            }
            catch (Exception ex)
            {
                _balloonError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[Notify] 气泡提示失败：{ex.Message}");
            }
        }

        if (config.SoundEnabled && !inQuietHours)
        {
            try
            {
                SystemSounds.Exclamation.Play();
                SoundCount++;
            }
            catch { /* 无音频设备时忽略 */ }
        }
    }

    /// <summary>静默时段内自动静音，其余配置不变。</summary>
    public void Notify(AlertRecord record, NotificationConfig config, QuietHoursConfig quiet, DateTime now)
    {
        var inQuiet = QuietHours.IsQuiet(quiet, now);

        if (inQuiet && quiet.SilenceSound)
        {
            var cloned = new NotificationConfig
            {
                ToastEnabled = config.ToastEnabled,
                BalloonEnabled = config.BalloonEnabled,
                PopupEnabled = config.PopupEnabled,
                PopupSeconds = config.PopupSeconds,
                SoundEnabled = false,
                AggregateEnabled = config.AggregateEnabled,
                AggregateWindowSeconds = config.AggregateWindowSeconds,
                MaxAggregateItems = config.MaxAggregateItems,
            };
            Notify(record, cloned, true);
            return;
        }

        Notify(record, config, inQuiet);
    }

    // ---------------------------------------------------------------- 自绘置顶窗

    /// <summary>
    /// 显示自绘置顶窗。若当前不在 UI 线程，则 Post 回 UI 线程执行。
    /// </summary>
    /// <remarks>
    /// 提醒是从聚合定时器线程发出的，而窗体只能在 UI 线程上创建。
    /// 但**自检按钮本身就在 UI 线程上**，所以那种情况下会走同步分支 ——
    /// 这样自检的计数能立刻反映结果，用户点完按钮就能看到「✓ / ✗」。
    /// </remarks>
    private void ShowPopup(AlertRecord record, NotificationConfig config)
    {
        var ctx = _uiContext;

        if (ctx is null || ReferenceEquals(SynchronizationContext.Current, ctx))
        {
            ShowPopupCore(record, config);
            return;
        }

        ctx.Post(_ => ShowPopupCore(record, config), null);
    }

    private void ShowPopupCore(AlertRecord record, NotificationConfig config)
    {
        try
        {
            AlertPopup.Show(record, config.PopupSeconds, _openLog);
            PopupCount++;
        }
        catch (Exception ex)
        {
            _popupError = $"{ex.GetType().Name}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[Notify] 置顶窗显示失败：{ex.Message}");
        }
    }


    public void Dispose()
    {
        // ToastNotificationManagerCompat 无需显式释放；保留接口以便将来扩展
    }
}


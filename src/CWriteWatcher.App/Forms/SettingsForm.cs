using System.ComponentModel;
using System.Diagnostics;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Etw;

namespace CWriteWatcher.App.Forms;

/// <summary>设置窗体。所有改动通过 <see cref="WatcherHost.Config"/> 持久化并即时生效。</summary>
internal sealed class SettingsForm : Form
{
    private readonly WatcherHost _host;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // 常规
    private readonly RadioButton _rbStandard = new() { Text = "标准模式（免管理员 · 写入量为估算值）", AutoSize = true };
    private readonly RadioButton _rbPrecise = new() { Text = "精确模式", AutoSize = true };
    private readonly CheckBox _chkAutoStart = new() { Text = "开机自动启动", AutoSize = true };
    private readonly Label _lblModeHint = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    private readonly Label _lblAutoStartHint = new() { AutoSize = true, MaximumSize = new Size(660, 0) };

    // 提醒
    private readonly RadioButton _rbModeA = new() { Text = "A · 任意新写入", AutoSize = true };
    private readonly RadioButton _rbModeB = new() { Text = "B · 超过 10 MB", AutoSize = true };
    private readonly RadioButton _rbModeC = new() { Text = "C · 超过 50 MB", AutoSize = true };
    private readonly CheckBox _chkCustomThreshold = new() { Text = "自定义阈值", AutoSize = true };
    private readonly NumericUpDown _numThresholdMb = new() { Minimum = 1, Maximum = 100_000, DecimalPlaces = 1, Width = 100 };
    private readonly ComboBox _cmbScope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _lblModeDescription = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    private readonly CheckBox _chkAnyWriteOnlyNew = new() { Text = "模式 A 只提醒「新建文件」（推荐，显著降低噪音）", AutoSize = true };
    private readonly CheckBox _chkProcessBurst = new() { Text = "同时启用「进程突发」告警（P3，噪音较高）", AutoSize = true };
    private readonly NumericUpDown _numProcessBurstWindow = new() { Minimum = 1, Maximum = 300, Width = 80 };
    private readonly CheckBox _chkAggregate = new() { Text = "聚合提醒（同进程短时间内的多条合并为一条）", AutoSize = true };
    private readonly NumericUpDown _numAggregateWindow = new() { Minimum = 1, Maximum = 60, Width = 80 };
    private readonly CheckBox _chkToast = new() { Text = "Windows Toast 通知", AutoSize = true };
    private readonly CheckBox _chkBalloon = new() { Text = "托盘气泡", AutoSize = true };
    private readonly CheckBox _chkSound = new() { Text = "声音提示", AutoSize = true };
    private readonly CheckBox _chkPopup = new() { Text = "自绘置顶提示窗（不经过系统通知，最可靠）", AutoSize = true };
    private readonly NumericUpDown _numPopupSeconds = new() { Minimum = 2, Maximum = 120, Width = 70 };

    /// <summary>自检结果标签。必须有初始文字 —— 界面冒烟测试会断言"分组框内不得有空标签"。</summary>
    private readonly Label _lblNotifyTest = new()
    {
        Text = "提醒通道自检：尚未测试",
        AutoSize = true,
        MaximumSize = new Size(660, 0),
        ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A),
    };

    // 范围
    private readonly CheckBox _chkWholeDrive = new() { Text = "监控整个 C 盘", AutoSize = true };
    private readonly ListBox _lstInclude = new() { Width = 480, Height = 110 };
    private readonly ListBox _lstExclude = new() { Width = 480, Height = 110 };
    private readonly NumericUpDown _numShallowDepth = new() { Minimum = 1, Maximum = 10, Width = 60 };
    private readonly NumericUpDown _numBufferKb = new() { Minimum = 4, Maximum = 1024, Width = 80 };
    private readonly NumericUpDown _numMaxDirs = new() { Minimum = 100, Maximum = 2_000_000, Width = 100 };

    // 排除
    private readonly DataGridView _gridRules = new();
    private readonly ListBox _lstProcesses = new() { Width = 300, Height = 200 };
    private readonly BindingList<ExclusionRule> _rules = new();

    // 防打扰
    private readonly NumericUpDown _numPerPath = new() { Minimum = 0, Maximum = 86_400, Width = 80 };
    private readonly NumericUpDown _numPerProcess = new() { Minimum = 0, Maximum = 86_400, Width = 80 };
    private readonly NumericUpDown _numGlobalRate = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.5M, Width = 80 };
    private readonly NumericUpDown _numGlobalBurst = new() { Minimum = 1, Maximum = 1000, Width = 80 };
    private readonly CheckBox _chkQuiet = new() { Text = "启用静默时段（仅记日志，不弹提醒）", AutoSize = true };
    private readonly DateTimePicker _dtQuietStart = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 100 };
    private readonly DateTimePicker _dtQuietEnd = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 100 };

    // 日志
    private readonly NumericUpDown _numMaxRows = new() { Minimum = 1000, Maximum = 10_000_000, Increment = 1000, Width = 120 };
    private readonly NumericUpDown _numMaxSizeMb = new() { Minimum = 10, Maximum = 10_000, Increment = 10, Width = 120 };
    private readonly CheckBox _chkAutoTrim = new() { Text = "自动裁剪（超出上限时删除最旧记录）", AutoSize = true };
    private readonly CheckBox _chkLogSuppressed = new() { Text = "记录被节流/静默抑制的事件", AutoSize = true };

    // 状态
    private readonly Label _lblStats = new() { AutoSize = true, Font = new Font("Consolas", 9f) };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    private bool _loading;

    public SettingsForm(WatcherHost host)
    {
        _host = host;

        Text = "CWriteWatcher · 设置";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 640);
        Size = new Size(820, 700);
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowInTaskbar = true;

        BuildUi();
        LoadFromConfig();

        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
    }

    // ---------------------------------------------------------------- UI 构建

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildAlertTab());
        _tabs.TabPages.Add(BuildScopeTab());
        _tabs.TabPages.Add(BuildExclusionTab());
        _tabs.TabPages.Add(BuildThrottleTab());
        _tabs.TabPages.Add(BuildLogTab());
        _tabs.TabPages.Add(BuildStatusTab());

        root.Controls.Add(_tabs, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        var btnOk = Ui.Btn("确定");
        var btnCancel = Ui.Btn("取消");
        var btnApply = Ui.Btn("应用");

        btnOk.Click += (_, _) => { if (ApplyFromUi()) Close(); };
        btnCancel.Click += (_, _) => Close();
        btnApply.Click += (_, _) => ApplyFromUi();

        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnApply);

        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    /// <summary>创建一个按内容自适应高度的分组框。</summary>
    /// <param name="title">分组标题。</param>
    /// <param name="minHeight">
    /// 最小高度。只用于保持版面观感；内容更高时会<b>自动增长</b>。
    /// 之所以不能写死高度：125% DPI 下字体变高、提示文字换行行数也会变多，
    /// 固定高度会把内容截断（且内层 AutoScroll 会冒出多余的滚动条）。
    /// </param>
    /// <param name="children">分组内的控件，自上而下排列。</param>
    private static GroupBox Group(string title, int minHeight, params Control[] children)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };

        foreach (var c in children) flow.Controls.Add(c);

        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, minHeight),
            Dock = DockStyle.Top,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 8),
        };

        box.Controls.Add(flow);
        return box;
    }

    private static TabPage Tab(string title, bool scroll = true)
    {
        return new TabPage(title)
        {
            AutoScroll = scroll,
            Padding = new Padding(10),
            UseVisualStyleBackColor = true,
        };
    }

    // ★ 注意：这里必须把 text 赋给 Text。曾经漏掉赋值，导致整个设置界面的
    //   提示与警告文字全部为空（占位但不显示），且编译器不会报错。
    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(680, 0),
        ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A),
    };

    private static Label Warn(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(680, 0),
        ForeColor = Color.FromArgb(0xA3, 0x2D, 0x2D),
    };

    private static FlowLayoutPanel Row(params Control[] children)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        foreach (var c in children) f.Controls.Add(c);
        return f;
    }

    private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };

    // ---------------------------------------------------------------- 各标签页

    private TabPage BuildGeneralTab()
    {
        var page = Tab("常规", scroll: false);

        _lblModeHint.ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A);
        _lblAutoStartHint.ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A);

        _rbStandard.CheckedChanged += (_, _) => UpdateModeHint();
        _rbPrecise.CheckedChanged += (_, _) => UpdateModeHint();

        var g1 = Group("采集模式", 170, _rbStandard, _rbPrecise, _lblModeHint);
        var g2 = Group("启动", 120, _chkAutoStart, _lblAutoStartHint);

        page.Controls.Add(g2);
        page.Controls.Add(g1);
        return page;
    }

    private TabPage BuildAlertTab()
    {
        var page = Tab("提醒");

        _rbModeA.CheckedChanged += (_, _) => UpdateModeHint();
        _rbModeB.CheckedChanged += (_, _) => UpdateModeHint();
        _rbModeC.CheckedChanged += (_, _) => UpdateModeHint();
        _chkCustomThreshold.CheckedChanged += (_, _) => UpdateModeHint();
        _numThresholdMb.ValueChanged += (_, _) => UpdateModeHint();
        _cmbScope.SelectedIndexChanged += (_, _) => UpdateModeHint();

        _cmbScope.Items.AddRange(new object[]
        {
            ThresholdScope.SameFileWindow.ToDisplayName(),
            ThresholdScope.SingleIrp.ToDisplayName(),
            ThresholdScope.ProcessBurst.ToDisplayName(),
        });

        _lblModeDescription.ForeColor = Color.FromArgb(0x63, 0x38, 0x06);

        var g1 = Group("触发模式", 210,
            _rbModeA, _rbModeB, _rbModeC,
            Row(_chkCustomThreshold, _numThresholdMb, Lbl("MB")),
            Row(Lbl("阈值判定口径："), _cmbScope),
            _lblModeDescription);

        var g2 = Group("高级", 160,
            _chkAnyWriteOnlyNew,
            Row(_chkProcessBurst, Lbl("窗口"), _numProcessBurstWindow, Lbl("秒")),
            Hint("P3「进程突发」的语义是「某进程在窗口内总共写了多少」，与「某文件被连续写入多少」完全不同，默认关闭。"));

        var btnTestNotify = Ui.Btn("发送测试提醒", 130);
        btnTestNotify.Click += (_, _) => SendTestNotification();

        var g3 = Group("提醒方式", 160,
            Row(_chkPopup, Lbl("显示"), _numPopupSeconds, Lbl("秒")),
            Row(_chkToast, _chkBalloon, _chkSound),
            Row(_chkAggregate, Lbl("聚合窗口"), _numAggregateWindow, Lbl("秒")),
            Row(btnTestNotify),
            _lblNotifyTest,
            Hint("「自绘置顶提示窗」是本程序自己画的顶层窗口，**不经过 Windows 通知平台** —— " +
                 "系统关闭「通知总开关」或开启「专注助手」时，Toast 和托盘气泡会一起失效，" +
                 "只有这条通道一定看得见。鼠标悬停可暂停倒计时，单击可打开日志。"));

        page.Controls.Add(g3);
        page.Controls.Add(g2);
        page.Controls.Add(g1);
        return page;
    }

    /// <summary>
    /// 走**与真实提醒完全相同**的代码路径发一条自检提醒，并把各通道的实际结果写回标签。
    /// </summary>
    /// <remarks>
    /// 排查「托盘图标变红了但没有弹窗」时的第一手段：
    /// <list type="bullet">
    ///   <item>自检显示「Toast ✓」而屏幕上没出现 → 是**系统**把通知吃了（专注助手 / 通知设置 / 应用被静音），
    ///         不是程序的问题；</item>
    ///   <item>自检显示「Toast 已降级」→ Toast 通道抛异常，已自动改用托盘气泡兜底；</item>
    ///   <item>两个通道都显示 ✓ 却都没看到 → 检查托盘图标是否在折叠区，以及系统通知总开关。</item>
    /// </list>
    /// </remarks>
    private void SendTestNotification()
    {
        try
        {
            var cfg = _host.Config.Current;

            // 用界面上**当前勾选**的通道，而不是已保存的配置 —— 用户可能刚改完还没点「应用」。
            var live = new NotificationConfig
            {
                PopupEnabled = _chkPopup.Checked,
                PopupSeconds = (int)_numPopupSeconds.Value,
                ToastEnabled = _chkToast.Checked,
                BalloonEnabled = _chkBalloon.Checked,
                SoundEnabled = _chkSound.Checked,
                AggregateEnabled = cfg.Notification.AggregateEnabled,
                AggregateWindowSeconds = cfg.Notification.AggregateWindowSeconds,
                MaxAggregateItems = cfg.Notification.MaxAggregateItems,
            };

            var n = _host.Notifier;
            var toastBefore = n.ToastCount;
            var balloonBefore = n.BalloonCount;
            var soundBefore = n.SoundCount;
            var popupBefore = n.PopupCount;

            n.SendTest(live, cfg.QuietHours, DateTime.Now);

            var toastOk = n.ToastCount > toastBefore;
            var balloonOk = n.BalloonCount > balloonBefore;
            var soundOk = n.SoundCount > soundBefore;
            var popupOk = n.PopupCount > popupBefore;

            var parts = new List<string>
            {
                live.PopupEnabled ? (popupOk ? "置顶窗 ✓" : "置顶窗 ✗") : "置顶窗（未启用）",
                live.ToastEnabled ? (toastOk ? "Toast ✓" : "Toast ✗") : "Toast（未启用）",
                live.BalloonEnabled ? (balloonOk ? "气泡 ✓" : "气泡 ✗") : "气泡（未启用）",
                live.SoundEnabled ? (soundOk ? "声音 ✓" : "声音 ✗") : "声音（未启用）",
            };

            var note = n.LastError is { } err ? $"　（通道错误：{err}）" : "";

            _lblNotifyTest.Text = $"提醒通道自检：{string.Join(" / ", parts)}{note}";

            // 只要有一条可见通道成功就算通过。置顶窗是唯一不受系统设置影响的那条，
            // 所以它成功时额外说明一句，避免用户误以为「Toast ✗」就是坏了。
            _lblNotifyTest.ForeColor = popupOk || toastOk || balloonOk
                ? Color.FromArgb(0x0F, 0x6C, 0x3B)
                : Color.FromArgb(0xA3, 0x2D, 0x2D);
        }
        catch (Exception ex)
        {
            _lblNotifyTest.Text = "提醒通道自检失败：" + ex.Message;
            _lblNotifyTest.ForeColor = Color.FromArgb(0xA3, 0x2D, 0x2D);
        }
    }

    private TabPage BuildScopeTab()
    {
        var page = Tab("监控范围");

        _chkWholeDrive.CheckedChanged += (_, _) => UpdateScopeHint();

        var btnAddInclude = Ui.Btn("添加目录…", 110);
        var btnDelInclude = Ui.Btn("移除", 70);
        btnAddInclude.Click += (_, _) => AddDirectory(_lstInclude);
        btnDelInclude.Click += (_, _) => RemoveSelected(_lstInclude);

        var btnAddExclude = Ui.Btn("添加目录…", 110);
        var btnDelExclude = Ui.Btn("移除", 70);
        btnAddExclude.Click += (_, _) => AddDirectory(_lstExclude);
        btnDelExclude.Click += (_, _) => RemoveSelected(_lstExclude);

        var g1 = Group("范围", 200,
            _chkWholeDrive,
            Hint("精确模式默认建议监控整个 C 盘（单会话覆盖，无句柄开销）。"),
            Warn("标准模式下勾选「整个 C 盘」会递归枚举十几万到几十万个目录，" +
                 "首次枚举需 30 秒 ~ 3 分钟，句柄与内存开销很大，可能超过 150 MB 目标。建议改用精确模式。"),
            Row(Lbl("系统目录浅层覆盖深度："), _numShallowDepth, Lbl("层")),
            Hint("标准模式未勾选全盘时，Program Files / Windows 只监控前 N 层，用于捕捉安装与更新事件。"));

        var g2 = Group("包含目录（递归监控）", 200,
            _lstInclude,
            Row(btnAddInclude, btnDelInclude),
            Hint("留空时使用默认范围：当前用户目录 + ProgramData（全递归）+ 系统目录浅层。"));

        var g3 = Group("排除目录", 200,
            _lstExclude,
            Row(btnAddExclude, btnDelExclude),
            Hint("这里排除的是整棵目录子树（在枚举阶段就跳过，可显著减少句柄数）。" +
                 "文件级的精细排除请在「排除表」页配置。"));

        var g4 = Group("性能", 140,
            Row(Lbl("单目录缓冲区："), _numBufferKb, Lbl("KB")),
            Row(Lbl("监控目录数上限："), _numMaxDirs),
            Hint("缓冲区总量受 64 MB 预算约束，目录数多时会自动下调单个缓冲区大小。"));

        page.Controls.Add(g4);
        page.Controls.Add(g3);
        page.Controls.Add(g2);
        page.Controls.Add(g1);
        return page;
    }

    private TabPage BuildExclusionTab()
    {
        var page = Tab("排除表", scroll: false);

        _gridRules.Dock = DockStyle.Fill;
        _gridRules.AllowUserToAddRows = false;
        _gridRules.AllowUserToDeleteRows = false;
        _gridRules.RowHeadersVisible = false;
        _gridRules.AutoGenerateColumns = false;
        _gridRules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gridRules.EditMode = DataGridViewEditMode.EditOnEnter;

        _gridRules.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(ExclusionRule.Enabled),
            HeaderText = "启用",
            Width = 50,
        });
        _gridRules.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ExclusionRule.Pattern),
            HeaderText = "规则（支持 * 通配）",
            Width = 300,
            ReadOnly = true,
        });
        _gridRules.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ExclusionRule.Category),
            HeaderText = "分类",
            Width = 110,
            ReadOnly = true,
        });
        _gridRules.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ExclusionRule.Note),
            HeaderText = "说明",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
        });

        var btnAddRule = Ui.Btn("添加自定义规则…", 140);
        var btnDelRule = Ui.Btn("删除选中", 100);
        var btnReset = Ui.Btn("恢复默认排除表", 140);

        btnAddRule.Click += (_, _) => AddCustomRule();
        btnDelRule.Click += (_, _) => DeleteSelectedRule();
        btnReset.Click += (_, _) => ResetRules();

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
        };
        topBar.Controls.AddRange(new Control[] { btnAddRule, btnDelRule, btnReset });

        var procBox = Group("进程名黑名单", 180,
            _lstProcesses,
            Row(MakeButton("添加进程…", (_, _) => AddProcess()), MakeButton("移除", (_, _) => RemoveSelected(_lstProcesses))),
            Hint("系统高频写入者（Defender、索引、Windows Update、杀软）默认已加入。"));

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_gridRules);
        host.Controls.Add(topBar);

        // 第 1 行按内容自适应，而不是写死 190px ——
        // 否则 125% DPI 下「进程名黑名单」分组会被截断（按钮变高 + 提示文字换行后装不下）。
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(host, 0, 0);
        layout.Controls.Add(procBox, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private static Button MakeButton(string text, EventHandler handler)
    {
        var b = Ui.Btn(text, 110);
        b.Click += handler;
        return b;
    }

    private TabPage BuildThrottleTab()
    {
        var page = Tab("防打扰");

        var g1 = Group("三层节流", 220,
            Row(Lbl("同一文件去重窗口："), _numPerPath, Lbl("秒（0 = 关闭）")),
            Row(Lbl("同一进程去重窗口："), _numPerProcess, Lbl("秒（0 = 关闭）")),
            Row(Lbl("全局提醒速率上限："), _numGlobalRate, Lbl("条 / 秒")),
            Row(Lbl("全局突发容量："), _numGlobalBurst, Lbl("条")),
            Hint("三层依次生效：文件级 → 进程级 → 全局令牌桶。任何一层拦下的事件仍会记入日志，" +
                 "只是不弹出提醒。默认 1 条 / 2 秒是防通知风暴的最后一道保险丝。"));

        var g2 = Group("静默时段", 170,
            _chkQuiet,
            Row(Lbl("从"), _dtQuietStart, Lbl("到"), _dtQuietEnd),
            Hint("支持跨零点区间，例如 23:00 – 07:00。静默时段内事件仍会记录到日志，只是不弹出提醒。"),
            Warn("静默时段内「声音提示」也会被一并静音。"));

        page.Controls.Add(g2);
        page.Controls.Add(g1);
        return page;
    }

    private TabPage BuildLogTab()
    {
        var page = Tab("日志");

        var btnOpen = Ui.Btn("打开日志查看器", 140);
        btnOpen.Click += (_, _) =>
        {
            var f = new LogForm(_host);
            f.Show();
        };

        var btnFolder = Ui.Btn("打开数据目录", 120);
        btnFolder.Click += (_, _) =>
        {
            try
            {
                Core.Util.PathUtil.EnsureDirectory(Core.Util.PathUtil.AppDataDir);
                Process.Start(new ProcessStartInfo(Core.Util.PathUtil.AppDataDir) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };

        var g1 = Group("保留策略", 200,
            Row(Lbl("最多保留条数："), _numMaxRows),
            Row(Lbl("数据库体积上限："), _numMaxSizeMb, Lbl("MB")),
            _chkAutoTrim,
            _chkLogSuppressed,
            Hint("超过上限时按「最旧优先」裁剪。内存超过 150 MB 时还会触发一次激进裁剪。"));

        var g2 = Group("操作", 110,
            Row(btnOpen, btnFolder),
            Hint($"数据目录：{Core.Util.PathUtil.AppDataDir}"));

        page.Controls.Add(g2);
        page.Controls.Add(g1);
        return page;
    }

    private TabPage BuildStatusTab()
    {
        // 允许滚动：状态项会随功能增加而变长（例如新增的「提醒通道」一段），
        // 固定高度容器一旦装不下就会静默裁掉底部内容。
        var page = Tab("状态");
        var g = Group("运行统计", 420, _lblStats,
            Hint("「路径映射命中率」只在精确模式下有意义：它反映有多少写入事件能拿到完整路径。" +
                 "程序启动前就已打开的长期句柄永远拿不到 Create 事件，会永久显示为未知路径。"));
        page.Controls.Add(g);
        return page;
    }

    // ---------------------------------------------------------------- 载入 / 保存

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            var c = _host.Config.Current;

            _rbStandard.Checked = c.CollectorMode == CollectorMode.Standard;
            _rbPrecise.Checked = c.CollectorMode == CollectorMode.Precise;
            _chkAutoStart.Checked = c.AutoStart;

            _rbModeA.Checked = c.TriggerMode == TriggerMode.AnyWrite;
            _rbModeB.Checked = c.TriggerMode == TriggerMode.Over10MB;
            _rbModeC.Checked = c.TriggerMode == TriggerMode.Over50MB;

            _chkCustomThreshold.Checked = c.CustomThresholdBytes is not null;
            _numThresholdMb.Value = Math.Clamp(
                (decimal)((c.CustomThresholdBytes ?? c.TriggerMode.DefaultThresholdBytes()) / (double)TriggerModeExtensions.MB),
                _numThresholdMb.Minimum, _numThresholdMb.Maximum);
            _numThresholdMb.Enabled = _chkCustomThreshold.Checked;

            _cmbScope.SelectedIndex = (int)c.ThresholdScope;
            _chkAnyWriteOnlyNew.Checked = c.AnyWriteOnlyNewFiles;
            _chkProcessBurst.Checked = c.ProcessBurstEnabled;
            _numProcessBurstWindow.Value = Math.Clamp(c.ProcessBurstWindowMs / 1000, 1, 300);

            _chkPopup.Checked = c.Notification.PopupEnabled;
            _numPopupSeconds.Value = Math.Clamp(c.Notification.PopupSeconds, 2, 120);
            _chkToast.Checked = c.Notification.ToastEnabled;
            _chkBalloon.Checked = c.Notification.BalloonEnabled;
            _chkSound.Checked = c.Notification.SoundEnabled;
            _chkAggregate.Checked = c.Notification.AggregateEnabled;
            _numAggregateWindow.Value = Math.Clamp(c.Notification.AggregateWindowSeconds, 1, 60);

            _chkWholeDrive.Checked = c.Scope.WholeDriveC;
            _lstInclude.Items.Clear();
            foreach (var d in c.Scope.IncludeDirectories) _lstInclude.Items.Add(d);
            _lstExclude.Items.Clear();
            foreach (var d in c.Scope.ExcludeDirectories) _lstExclude.Items.Add(d);
            _numShallowDepth.Value = Math.Clamp(c.Scope.ShallowDepthForSystemDirs, 1, 10);
            _numBufferKb.Value = Math.Clamp(c.Scope.BufferSizeKb, 4, 1024);
            _numMaxDirs.Value = Math.Clamp(c.Scope.MaxMonitoredDirectories, 100, 2_000_000);

            _rules.Clear();
            foreach (var r in c.Exclusions.Rules) _rules.Add(r);
            _gridRules.DataSource = _rules;

            _lstProcesses.Items.Clear();
            foreach (var p in c.Exclusions.ExcludedProcesses) _lstProcesses.Items.Add(p);

            _numPerPath.Value = Math.Clamp(c.Throttle.PerPathSeconds, 0, 86_400);
            _numPerProcess.Value = Math.Clamp(c.Throttle.PerProcessSeconds, 0, 86_400);
            _numGlobalRate.Value = Math.Clamp((decimal)c.Throttle.GlobalTokensPerSecond, 0, 100);
            _numGlobalBurst.Value = Math.Clamp(c.Throttle.GlobalBurst, 1, 1000);

            _chkQuiet.Checked = c.QuietHours.Enabled;
            _dtQuietStart.Value = ParseTime(c.QuietHours.Start, "23:00");
            _dtQuietEnd.Value = ParseTime(c.QuietHours.End, "07:00");

            _numMaxRows.Value = Math.Clamp(c.Logging.MaxRows, 1000, 10_000_000);
            _numMaxSizeMb.Value = Math.Clamp(c.Logging.MaxSizeMb, 10, 10_000);
            _chkAutoTrim.Checked = c.Logging.AutoTrim;
            _chkLogSuppressed.Checked = c.Logging.LogSuppressed;
        }
        finally
        {
            _loading = false;
        }

        UpdateModeHint();
        UpdateScopeHint();
    }

    private static DateTime ParseTime(string text, string fallback)
    {
        if (!TimeSpan.TryParse(text, out var ts)) TimeSpan.TryParse(fallback, out ts);
        return DateTime.Today.Add(ts);
    }

    private bool ApplyFromUi()
    {
        _gridRules.EndEdit();

        var cfg = _host.Config.Current.Clone();

        var newMode = _rbPrecise.Checked ? CollectorMode.Precise : CollectorMode.Standard;
        var modeChanged = newMode != cfg.CollectorMode;

        if (modeChanged && newMode == CollectorMode.Precise && !EtwKernelFileCollector.IsAvailable)
        {
            var answer = MessageBox.Show(
                "精确模式需要管理员权限。\n\n" +
                "是否立即以管理员身份重新启动程序？\n" +
                "（选择「否」将保持标准模式）",
                "需要管理员权限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return false;

            cfg.CollectorMode = CollectorMode.Precise;
            SaveConfig(cfg);
            RelaunchElevated();
            return false;
        }

        cfg.CollectorMode = newMode;

        cfg.TriggerMode = _rbModeA.Checked ? TriggerMode.AnyWrite
            : _rbModeC.Checked ? TriggerMode.Over50MB
            : TriggerMode.Over10MB;

        cfg.CustomThresholdBytes = _chkCustomThreshold.Checked
            ? (long)(_numThresholdMb.Value * TriggerModeExtensions.MB)
            : null;

        cfg.ThresholdScope = (ThresholdScope)Math.Clamp(_cmbScope.SelectedIndex, 0, 2);
        cfg.AnyWriteOnlyNewFiles = _chkAnyWriteOnlyNew.Checked;
        cfg.ProcessBurstEnabled = _chkProcessBurst.Checked;
        cfg.ProcessBurstWindowMs = (int)(_numProcessBurstWindow.Value * 1000);

        cfg.Notification.PopupEnabled = _chkPopup.Checked;
        cfg.Notification.PopupSeconds = (int)_numPopupSeconds.Value;
        cfg.Notification.ToastEnabled = _chkToast.Checked;
        cfg.Notification.BalloonEnabled = _chkBalloon.Checked;
        cfg.Notification.SoundEnabled = _chkSound.Checked;
        cfg.Notification.AggregateEnabled = _chkAggregate.Checked;
        cfg.Notification.AggregateWindowSeconds = (int)_numAggregateWindow.Value;

        cfg.Scope.WholeDriveC = _chkWholeDrive.Checked;
        cfg.Scope.IncludeDirectories = _lstInclude.Items.Cast<string>().ToList();
        cfg.Scope.ExcludeDirectories = _lstExclude.Items.Cast<string>().ToList();
        cfg.Scope.ShallowDepthForSystemDirs = (int)_numShallowDepth.Value;
        cfg.Scope.BufferSizeKb = (int)_numBufferKb.Value;
        cfg.Scope.MaxMonitoredDirectories = (int)_numMaxDirs.Value;

        cfg.Exclusions.Rules = _rules.ToList();
        cfg.Exclusions.ExcludedProcesses = _lstProcesses.Items.Cast<string>().ToList();

        cfg.Throttle.PerPathSeconds = (int)_numPerPath.Value;
        cfg.Throttle.PerProcessSeconds = (int)_numPerProcess.Value;
        cfg.Throttle.GlobalTokensPerSecond = (double)_numGlobalRate.Value;
        cfg.Throttle.GlobalBurst = (int)_numGlobalBurst.Value;

        cfg.QuietHours.Enabled = _chkQuiet.Checked;
        cfg.QuietHours.Start = _dtQuietStart.Value.ToString("HH:mm");
        cfg.QuietHours.End = _dtQuietEnd.Value.ToString("HH:mm");

        cfg.Logging.MaxRows = (int)_numMaxRows.Value;
        cfg.Logging.MaxSizeMb = (int)_numMaxSizeMb.Value;
        cfg.Logging.AutoTrim = _chkAutoTrim.Checked;
        cfg.Logging.LogSuppressed = _chkLogSuppressed.Checked;

        // 开机自启：先删后写（v0.2 §4.4）
        var autoStartChanged = cfg.AutoStart != _chkAutoStart.Checked;
        if (autoStartChanged)
        {
            var location = cfg.CollectorMode == CollectorMode.Precise
                ? AutoStartLocation.ScheduledTask
                : AutoStartLocation.RunKey;

            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            if (!AutoStartManager.Apply(_chkAutoStart.Checked, location, exePath, out var error))
            {
                MessageBox.Show($"设置开机自启失败：\n{error}", "CWriteWatcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            cfg.AutoStart = _chkAutoStart.Checked;
            cfg.AutoStartLocation = _chkAutoStart.Checked ? location : AutoStartLocation.None;
        }

        SaveConfig(cfg);
        UpdateModeHint();
        UpdateScopeHint();
        return true;
    }

    private void SaveConfig(AppConfig cfg) => _host.Config.Save(cfg);

    private void RelaunchElevated()
    {
        // 消息循环已在运行，提权成功后需要显式退出当前实例
        if (ElevationHelper.RelaunchElevated()) Application.Exit();
    }

    // ---------------------------------------------------------------- 提示文案

    private void UpdateModeHint()
    {
        if (_loading && !IsHandleCreated) { /* 仍在初始化 */ }

        var precise = _rbPrecise.Checked;
        _lblModeHint.Text = precise
            ? "精确模式：订阅 ETW Microsoft-Windows-Kernel-File 实时会话，可拿到真实的单次写入字节数与发起进程，" +
              "单会话覆盖整个卷。需要管理员权限；切换模式后需要重启程序。"
            : "标准模式：使用 ReadDirectoryChangesW + 文件大小差分估算。免管理员、开箱即用，" +
              "但写入量是估算值、拿不到发起进程，且「原地覆盖写」会漏报。";

        _lblAutoStartHint.Text = precise
            ? "精确模式的自启使用「任务计划程序」并以最高权限运行（登录时静默启动，不弹 UAC）。" +
              "创建任务需要一次管理员授权。切换模式时会自动清理另一种模式遗留的自启项。"
            : "标准模式的自启写入 HKCU\\...\\Run，无需管理员权限，开关即时生效。";

        var threshold = _chkCustomThreshold.Checked
            ? (long)(_numThresholdMb.Value * TriggerModeExtensions.MB)
            : (_rbModeC.Checked ? TriggerMode.Over50MB : TriggerMode.Over10MB).DefaultThresholdBytes();

        var mode = _rbModeA.Checked ? TriggerMode.AnyWrite
            : _rbModeC.Checked ? TriggerMode.Over50MB
            : TriggerMode.Over10MB;

        var scope = (ThresholdScope)Math.Clamp(_cmbScope.SelectedIndex, 0, 2);
        _lblModeDescription.Text = mode.ToDescription(threshold, scope);
    }

    private void UpdateScopeHint()
    {
        _lstInclude.Enabled = true;
        _lstExclude.Enabled = true;
    }

    private void RefreshStats()
    {
        try
        {
            var s = _host.GetSnapshot();
            var paused = _host.IsPaused;

            _lblStats.Text = string.Join(Environment.NewLine, new[]
            {
                $"运行状态        {(paused ? "已暂停" : "运行中")}",
                $"采集模式        {_host.Config.Current.CollectorMode.ToDisplayName()}",
                $"采集器状态      {_host.CollectorStatus}",
                $"治理级别        {s.Level.ToDisplayName()}   {_host.Pipeline?.Governor.DescribeReason()}",
                $"CPU（占整机）   {s.CpuPercent:0.##} %",
                $"内存（工作集）  {s.MemoryMb:0.#} MB",
                $"事件速率        {s.EventsPerSecond:0.#} 条 / 秒",
                "",
                $"接收事件        {s.Received}",
                $"路径排除        {s.ExcludedByPath}",
                $"进程排除        {s.ExcludedByProcess}",
                $"自身排除        {s.ExcludedBySelf}",
                $"未知路径        {s.UnknownPath}",
                $"低于阈值        {s.BelowThreshold}",
                $"触发提醒        {s.Triggered}   （实际弹出 {s.Notified}）",
                $"静默抑制        {s.SuppressedQuietHours}",
                $"节流抑制        {s.SuppressedThrottle}",
                $"降级丢弃        {s.SuppressedGovernance}",
                $"队列积压        {s.QueueLength} / {s.QueueCapacity}  ({s.QueueFillPercent:0.#} %)",
                // ★ 采集侧丢失的事件。非零意味着日志不完整，必须显式提示 ——
                //   这是本工具最该避免的失败类型（静默少记，用户却以为看到了全部）。
                $"丢失事件        {s.LostEvents}{DescribeLostEvents(s.LostEvents)}",
                "",
                $"提醒通道        {_host.Notifier.DescribeChannels(_host.Config.Current.Notification)}",
                $"已发送          Toast {_host.Notifier.ToastCount} · 气泡 {_host.Notifier.BalloonCount} · 置顶窗 {_host.Notifier.PopupCount} · 声音 {_host.Notifier.SoundCount}",
                $"通道错误        {_host.Notifier.LastError ?? "（无）"}",
                "",
                $"监控目录数      {s.MonitoredDirectories}",
                $"打开句柄数      {s.HandleCount}",
                $"路径映射命中率  {s.PathMapHitRate:0.##} %",
            });
        }
        catch { /* 关闭中 */ }
    }

    /// <summary>
    /// 给"丢失事件"计数配一句人话解释 —— 光看数字用户不知道这意味着什么。
    /// </summary>
    /// <remarks>
    /// 两种模式的语义不同（见 <c>CollectorStats.LostEvents</c>），所以提示也要分开写。
    /// </remarks>
    private string DescribeLostEvents(long lost)
    {
        if (lost <= 0) return "   （无丢失）";

        return _host.Config.Current.CollectorMode == CollectorMode.Precise
            ? "   ← 日志不完整：ETW 缓冲区不足，这些事件已无法补回"
            : "   ← 日志不完整：目录通知缓冲区溢出，丢失条数不可知";
    }

    // ---------------------------------------------------------------- 小工具

    private void AddDirectory(ListBox target)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择目录（仅 C 盘目录会被监控）",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var p = Core.Util.PathUtil.Normalize(dlg.SelectedPath);
        if (p.Length == 0 || target.Items.Contains(p)) return;
        target.Items.Add(p);
    }

    private static void RemoveSelected(ListBox list)
    {
        if (list.SelectedIndex < 0) return;
        list.Items.RemoveAt(list.SelectedIndex);
    }

    private void AddCustomRule()
    {
        var pattern = Prompt.Show(this, "添加排除规则",
            "输入路径或通配模式，例如：\n" +
            "  C:\\MyApp\\Logs\\*      排除整个目录\n" +
            "  *.bak                  排除所有 .bak 文件\n" +
            "  D:\\Temp\\cache.tmp     排除指定文件");
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var normalized = Core.Util.PathUtil.NormalizeRule(pattern);
        if (normalized.Length == 0) return;

        _rules.Add(new ExclusionRule
        {
            Pattern = normalized,
            Enabled = true,
            IsBuiltIn = false,
            Category = "自定义",
            Note = "用户添加",
        });
    }

    private void DeleteSelectedRule()
    {
        if (_gridRules.CurrentRow?.DataBoundItem is not ExclusionRule rule) return;
        if (rule.IsBuiltIn)
        {
            var answer = MessageBox.Show(
                "这是内置规则。内置规则建议通过「启用」列关闭，而不是删除。\n\n仍要删除吗？",
                "CWriteWatcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        _rules.Remove(rule);
    }

    private void ResetRules()
    {
        var answer = MessageBox.Show(
            "将恢复为默认排除表，你添加的自定义规则会被清除。是否继续？",
            "CWriteWatcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        _rules.Clear();
        foreach (var r in Core.Filtering.DefaultExclusions.BuildPathRules()) _rules.Add(r);

        _lstProcesses.Items.Clear();
        foreach (var p in Core.Filtering.DefaultExclusions.BuildProcessNames()) _lstProcesses.Items.Add(p);
    }

    private void AddProcess()
    {
        var name = Prompt.Show(this, "添加进程名", "输入进程文件名，例如：myapp.exe");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_lstProcesses.Items.Contains(name)) _lstProcesses.Items.Add(name);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statsTimer.Stop();
        _statsTimer.Dispose();
        base.OnFormClosing(e);
    }
}

/// <summary>一个极简的文本输入对话框（WinForms 没有内置的）。</summary>
internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string message)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 190),
            Font = new Font("Microsoft YaHei UI", 9f),
        };

        var label = new Label
        {
            Text = message,
            Left = 16,
            Top = 14,
            Width = 428,
            Height = 90,
            ForeColor = Color.FromArgb(0x44, 0x44, 0x41),
        };

        var input = new TextBox { Left = 16, Top = 112, Width = 428 };

        var ok = Ui.Btn("确定", 80);
        ok.Left = 272;
        ok.Top = 146;
        ok.DialogResult = DialogResult.OK;

        var cancel = Ui.Btn("取消", 80);
        cancel.Left = 364;
        cancel.Top = 146;
        cancel.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { label, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}

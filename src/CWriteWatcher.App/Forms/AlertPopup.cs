using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Drawing.Text;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Util;

namespace CWriteWatcher.App.Forms;

/// <summary>
/// 自绘的置顶提醒窗（右下角卡片，Win11 Fluent 风格）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它：</b>Win10/11 上 Windows Toast 与
/// <c>NotifyIcon.ShowBalloonTip</c> 走的是<b>同一条通知管道</b>，
/// 只要系统级的「通知总开关」（<c>HKCU\...\PushNotifications\ToastEnabled</c>）
/// 或「专注助手 / 勿扰」生效，两条通道会<b>一起</b>被静默 ——
/// 而且 <c>ToastContentBuilder.Show()</c> 不会抛异常，API 层面完全看不出来。
/// 这个窗口是我们自己画的顶层窗口，不经过通知平台，任何系统设置都挡不住。</para>
/// <para><b>不抢焦点的实现要点：</b>
/// <list type="bullet">
///   <item><see cref="ShowWithoutActivation"/> 返回 <c>true</c>；</item>
///   <item><see cref="CreateParams"/> 里加 <c>WS_EX_NOACTIVATE</c>（0x08000000）；</item>
///   <item>加 <c>WS_EX_TOOLWINDOW</c>（0x00000080），避免出现在 Alt-Tab 里；</item>
///   <item><see cref="Form.ShowInTaskbar"/> = <c>false</c>，不占任务栏。</item>
/// </list>
/// </para>
/// <para><b>长文本：</b>主行只显示<b>文件名</b>（单行 + 省略号截断），完整路径放在 Tooltip 里；
/// 右侧的文件夹按钮可直接在资源管理器里定位。**任何情况下都不允许长路径撑破布局。**</para>
/// <para><b>图标：</b>WinForms 没有 SVG 运行时，为两个图标引第三方库不划算，
/// 因此关闭按钮与文件夹按钮都用 <b>GDI+ 矢量绘制</b> —— 效果等价：
/// 分辨率无关、任意 DPI 下都锐利，且不依赖任何外部资源文件。</para>
/// </remarks>
internal sealed class AlertPopup : Form
{
    // ---------------------------------------------------------------- 逻辑尺寸（96 DPI 基准，运行时按 DPI 缩放）

    private const int LogicalCardWidth = 400;
    private const int ShadowPad = 14;      // 投影留白
    private const int CardPad = 16;        // 卡片内边距（要求 16px+）
    private const int Radius = 8;          // 圆角 8px
    private const int AccentWidth = 4;
    private const int IconButtonSize = 26;
    private const int CloseButtonSize = 28;
    private const int GapAfterTitle = 12;
    private const int GapTight = 6;
    private const int GapWide = 12;

    private const int StackGap = 12;
    private const int ScreenMargin = 18;
    private const int MaxVisibleCards = 3;

    // ---------------------------------------------------------------- 蓝色渐变配色
    //
    // ★ 卡片底与左侧强调条都用**蓝色渐变**：
    //   - 强调条：浅蓝 → 深蓝（竖向），是全卡片最强的视觉锚点
    //   - 卡片底：近白的淡蓝 → 略深的淡蓝，克制但能看出渐变
    //   文字强调统一用 AccentBlue（纯色，保证小字号的对比度与可读性）。
    private static readonly Color BarGradientTop = Color.FromArgb(0x4A, 0x9E, 0xFF);
    private static readonly Color BarGradientBottom = Color.FromArgb(0x0F, 0x6C, 0xBD);
    private static readonly Color AccentBlue = Color.FromArgb(0x0F, 0x6C, 0xBD);

    private static readonly Color CardGradientTop = Color.FromArgb(0xF7, 0xFA, 0xFF);
    private static readonly Color CardGradientBottom = Color.FromArgb(0xE1, 0xEC, 0xFA);
    private static readonly Color CardHoverTop = Color.FromArgb(0xEF, 0xF6, 0xFF);
    private static readonly Color CardHoverBottom = Color.FromArgb(0xD6, 0xE5, 0xF7);

    private static readonly Color CardBorder = Color.FromArgb(0xDA, 0xE6, 0xF3);
    private static readonly Color TextPrimary = Color.FromArgb(0x16, 0x20, 0x2C);
    private static readonly Color TextBody = Color.FromArgb(0x33, 0x40, 0x4F);
    private static readonly Color TextMuted = Color.FromArgb(0x78, 0x83, 0x8F);
    private static readonly Color HoverBack = Color.FromArgb(0xE1, 0xEB, 0xF8);

    private static readonly List<AlertPopup> Open = new();

    private readonly AlertRecord _record;
    private readonly int _autoCloseMs;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly ToolTip _tip = new();

    private Font _titleFont = null!;
    private Font _emphasisFont = null!;
    private Font _bodyFont = null!;
    private Font _metaFont = null!;

    private float _scale = 1f;

    // 文本
    private string _title = string.Empty;
    private string _process = string.Empty;
    private string _size = string.Empty;
    private string _sizeNote = string.Empty;
    private string _fileName = string.Empty;
    private string _metaLeft = string.Empty;
    private string _linkText = "单击查看日志";
    private string _fullPath = string.Empty;

    // 布局
    private Rectangle _cardRect;
    private Rectangle _titleRect;
    private Rectangle _processRowRect;
    private Rectangle _fileRowRect;
    private Rectangle _metaRowRect;
    private Rectangle _closeRect;
    private Rectangle _folderRect;
    private Rectangle _linkRect;
    private int _accentWidth;

    // 交互状态
    private bool _hoverCard;
    private bool _hoverClose;
    private bool _hoverFolder;
    private bool _hoverLink;
    private bool _folderAvailable;
    private bool _tipShown;
    private string _tipText = string.Empty;

    private int _remainingMs;
    private bool _closing;

    /// <summary>当前渲染出的 32bpp 带 alpha 的画面（分层窗口直接提交它）。</summary>
    private Bitmap? _surface;

    /// <summary>整体不透明度，用于最后 400ms 淡出（分层窗口下不能再用 <see cref="Form.Opacity"/>）。</summary>
    private byte _alpha = 255;

    /// <summary>离屏测量用的 Graphics（分辨率与窗口 DPI 一致）。</summary>
    private Bitmap? _measureBitmap;
    private Graphics? _measureGraphics;
    private int _measureDpi;

    /// <summary>单击卡片时触发（默认用于打开日志窗口）。</summary>
    public Action? OpenLogRequested { get; set; }

    private AlertPopup(AlertRecord record, int autoCloseSeconds)
    {
        _record = record;
        _autoCloseMs = Math.Clamp(autoCloseSeconds, 2, 120) * 1000;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = "C 盘写入提醒";

        _tip.InitialDelay = 300;
        _tip.ReshowDelay = 100;
        _tip.AutoPopDelay = 20_000;

        // 分层窗口完全不走 WM_PAINT：全部内容由 RenderSurface 画进带 alpha 的位图后
        // 用 UpdateLayeredWindow 提交。关掉系统绘制可以彻底消除"先擦背景再画"造成的闪烁。
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint,
            true);

        _tick = new System.Windows.Forms.Timer { Interval = 50 };
        _tick.Tick += OnTick;
    }

    /// <summary>显示一条提醒卡片。必须在 UI 线程调用。</summary>
    public static void Show(AlertRecord record, int autoCloseSeconds, Action? openLog)
    {
        // 超出上限时先关掉最旧的，避免刷屏
        while (Open.Count >= MaxVisibleCards) Open[0].CloseNow();

        var popup = new AlertPopup(record, autoCloseSeconds) { OpenLogRequested = openLog };
        Open.Add(popup);
        popup.Show();
        RepositionStack();
    }

    // ---------------------------------------------------------------- 不抢焦点

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | Layered.WS_EX_LAYERED;
            return cp;
        }
    }

    // ---------------------------------------------------------------- 分层窗口渲染

    /// <remarks>
    /// <b>为什么必须用分层窗口（<c>UpdateLayeredWindow</c>）：</b>
    /// 普通 WinForms 窗口会用 <see cref="Control.BackColor"/> 铺满**整个客户区**，
    /// 而我们只在其中画了卡片 —— 卡片外侧那圈"投影留白"就会显示成一块
    /// 浅灰色底板（用户看到的"白板"）。
    /// <para>能同时做到「透明外侧 + 圆角 + 柔和半透明投影」的只有逐像素 alpha 的分层窗口：
    /// <c>TransparencyKey</c> 是 1 位掩码（投影会被切成硬边、圆角会有锯齿），
    /// <c>Form.Opacity</c> 是整窗等比淡出（同样无法只让外侧透明）。</para>
    /// <para><b>文字渲染</b>：必须用 <b>GDI+ 的 <c>DrawString</c></b>（见
    /// <see cref="DrawSingleLine"/>）。最初用了 <c>TextRenderer</c>（GDI），
    /// 而 <b>GDI 的 ClearType 在带 alpha 通道的位图上会退化成"无抗锯齿"</b>，
    /// 文字变成又粗又硬的块状 —— 这正是用户反馈的"越改越糊"。</para>
    /// </remarks>
    private void RenderSurface()
    {
        if (!IsHandleCreated || _closing) return;

        var w = Width;
        var h = Height;
        if (w <= 0 || h <= 0) return;

        _surface ??= new Bitmap(w, h, PixelFormat.Format32bppArgb);
        if (_surface.Width != w || _surface.Height != h)
        {
            _surface.Dispose();
            _surface = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

        // 位图分辨率与窗口 DPI 对齐。
        //
        // 说明（实测过，别想当然）：`new Bitmap(w,h,32bppArgb)` 在 **DPI 感知进程**里
        // 默认分辨率就等于屏幕 DPI（本机 125% → 120），所以这一行在本机是**空操作**。
        // 它真正起作用的场景是**多显示器混合缩放**：
        // 进程是 PerMonitorV2 时，`new Bitmap` 取的是**系统 DPI**（主屏），
        // 而 `DeviceDpi` 是窗口所在**那块屏**的 DPI —— 两者可能不同，
        // 不显式设置就会按错误的 DPI 栅格化文字。保留它是为了这个场景。
        _surface.SetResolution(DeviceDpi, DeviceDpi);

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);          // ★ 外侧保持全透明
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 文字**始终画在不透明的卡片上**，所以 ClearType 可用。
            // （前提是绘制走 GDI+；GDI 的 TextRenderer 在带 alpha 位图上会退化。）
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DrawShadow(g);
            DrawCard(g);
            DrawContent(g);
        }

        Present(_alpha);
    }

    /// <summary>把当前 surface 提交给窗口（可只改整体透明度，不必重画）。</summary>
    private void Present(byte alpha)
    {
        if (_surface is null || !IsHandleCreated || _closing) return;

        Layered.Present(Handle, _surface, Left, Top, alpha);
    }

    // 分层窗口不做 WM_PAINT 绘制，这里全部空实现。
    protected override void OnPaintBackground(PaintEventArgs e) { /* 由 RenderSurface 统一提交 */ }

    protected override void OnPaint(PaintEventArgs e) { /* 由 RenderSurface 统一提交 */ }

    // ---------------------------------------------------------------- 布局

    /// <remarks>
    /// <b>为什么在 <see cref="OnLoad"/> 而不是 <see cref="OnHandleCreated"/> 里测量：</b>
    /// 在 <c>OnHandleCreated</c> 阶段窗口的非客户区尺寸还没定下来，
    /// <c>Form.SetClientSizeCore</c> 会用<b>旧</b>的非客户区差值去反算窗口大小，
    /// 结果高度被多加约 39 px（实测 114 → 153），卡片底部出现一条空白。
    /// <c>OnLoad</c> 在 <c>Show()</c> 内部、首次绘制之前触发，此时尺寸已经可靠。
    /// </remarks>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CreateFonts();
        LayoutCard();
        _remainingMs = _autoCloseMs;
        _tick.Start();

        RenderSurface();   // 分层窗口不靠 WM_PAINT，必须显式提交首帧
    }

    private void CreateFonts()
    {
        var family = Font.FontFamily;
        _titleFont = new Font(family, 11f, FontStyle.Bold, GraphicsUnit.Point);
        _emphasisFont = new Font(family, 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        _bodyFont = new Font(family, 10f, FontStyle.Regular, GraphicsUnit.Point);
        _metaFont = new Font(family, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    private int P(int logical) => (int)Math.Round(logical * _scale);

    private void LayoutCard()
    {
        _scale = DeviceDpi / 96f;

        var cardWidth = P(LogicalCardWidth);
        var pad = P(CardPad);
        _accentWidth = P(AccentWidth);
        var contentWidth = cardWidth - pad * 2 - _accentWidth;

        var left = pad + _accentWidth;
        var fileTextWidth = contentWidth - P(IconButtonSize) - P(8);

        BuildTexts(fileTextWidth);

        // 标题要给右上角的关闭按钮让位，避免文字压到按钮上
        var titleWidth = contentWidth - P(CloseButtonSize) - P(8);

        var titleH = Measure(_title, _titleFont, titleWidth).Height;
        var processH = Math.Max(
            Measure(_process, _emphasisFont, contentWidth).Height,
            Measure(_size, _emphasisFont, contentWidth).Height);
        var fileH = Math.Max(Measure(_fileName, _bodyFont, contentWidth).Height, P(IconButtonSize));
        var metaH = Measure(_metaLeft, _metaFont, contentWidth).Height;

        var y = P(CardPad);

        _titleRect = new Rectangle(left, y, titleWidth, titleH);
        y += titleH + P(GapAfterTitle);

        _processRowRect = new Rectangle(left, y, contentWidth, processH);
        y += processH + P(GapTight);

        _fileRowRect = new Rectangle(left, y, contentWidth, fileH);
        y += fileH + P(GapWide);

        _metaRowRect = new Rectangle(left, y, contentWidth, metaH);
        y += metaH + P(CardPad);

        _cardRect = new Rectangle(P(ShadowPad), P(ShadowPad), cardWidth, y);
        ClientSize = new Size(cardWidth + P(ShadowPad) * 2, y + P(ShadowPad) * 2);

        var close = P(CloseButtonSize);
        _closeRect = new Rectangle(_cardRect.Right - close - P(6), _cardRect.Top + P(6), close, close);

        var icon = P(IconButtonSize);
        _folderRect = new Rectangle(_fileRowRect.Right - icon, _fileRowRect.Top + (fileH - icon) / 2, icon, icon);

        var linkW = MeasureWidth(_linkText, _metaFont);
        _linkRect = new Rectangle(_metaRowRect.Right - linkW, _metaRowRect.Top, linkW, metaH);
    }

    private void BuildTexts(int fileTextWidth)
    {
        _title = $"C 盘写入提醒 · {_record.TriggerMode}";
        _process = _record.DisplayProcess;
        _size = _record.DisplaySize;
        _sizeNote = _record.SizeIsExact ? string.Empty : "（估算）";
        _metaLeft = $"{_record.Timestamp:HH:mm:ss} · {_record.CollectorMode}";
        _fullPath = _record.DisplayPath;

        // 只保留文件名（长路径的核心处理方式）；聚合提醒与未知路径不是单个文件，原样显示
        _fileName = BuildFileName(_record);
        _folderAvailable = _record.AggregatedCount <= 1 && PathUtil.IsShellSafePath(_record.Path);

        // 再按可用宽度做一次省略号截断 —— 绝不撑破布局
        _fileName = Ellipsize(_fileName, _bodyFont, Math.Max(1, fileTextWidth));
    }

    /// <summary>从完整路径取文件名。聚合提醒（"… 等 3 个文件"）与未知路径原样返回。</summary>
    private static string BuildFileName(AlertRecord r)
    {
        if (r.AggregatedCount > 1) return r.DisplayPath;
        if (string.IsNullOrEmpty(r.Path)) return r.DisplayPath;

        try
        {
            var name = Path.GetFileName(r.Path);
            return string.IsNullOrEmpty(name) ? r.DisplayPath : name;
        }
        catch
        {
            return r.DisplayPath;   // 路径含非法字符时兜底
        }
    }

    // ---------------------------------------------------------------- 测量助手

    /// <summary>
    /// 文字测量。
    /// </summary>
    /// <remarks>
    /// ★ 必须用 GDI+ 的 <see cref="Graphics.MeasureString(string, Font, int, StringFormat)"/>，
    /// 而不是 <c>TextRenderer.MeasureText</c> —— 因为**绘制也走 GDI+**（见下），
    /// 测量与绘制必须是同一个引擎，否则布局与实际字形会有细微偏差。
    /// </remarks>
    private Size Measure(string text, Font font, int maxWidth)
    {
        var g = EnsureMeasureGraphics();
        var size = g.MeasureString(text, font, Math.Max(1, maxWidth), TypographicWrap);
        return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    private int MeasureWidth(string text, Font font)
    {
        var g = EnsureMeasureGraphics();
        var size = g.MeasureString(text, font, int.MaxValue, TypographicSingleLine);
        return (int)Math.Ceiling(size.Width);
    }

    /// <summary>
    /// 用于测量的离屏 Graphics（分辨率必须与窗口 DPI 一致）。
    /// </summary>
    /// <remarks>
    /// 若窗口被拖到不同缩放的显示器，<see cref="Control.DeviceDpi"/> 会变 ——
    /// 此时旧 Graphics 的分辨率已过时，必须重建，否则布局会按旧 DPI 算。
    /// </remarks>
    private Graphics EnsureMeasureGraphics()
    {
        if (_measureGraphics is not null && _measureDpi == DeviceDpi) return _measureGraphics;

        _measureGraphics?.Dispose();
        _measureBitmap?.Dispose();

        _measureDpi = DeviceDpi;
        _measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        _measureBitmap.SetResolution(_measureDpi, _measureDpi);

        _measureGraphics = Graphics.FromImage(_measureBitmap);
        _measureGraphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        return _measureGraphics;
    }

    // GDI+ 的排版格式：GenericTypographic 去掉多余的行距与内边距，接近 GDI 的紧凑度量。
    //
    // ★ MeasureTrailingSpaces 必须开：混排时用 "  写入了  " 靠空格制造间距，
    //   而 GDI+ 默认**不把行尾空格计入宽度** → 测量偏窄 → 相邻段会贴在一起。
    private static readonly StringFormat TypographicSingleLine = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                      | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
    };

    private static readonly StringFormat TypographicWrap = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoClip,
    };

    private static readonly StringFormat TypographicRight = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
    };

    /// <summary>单行显示：超出宽度时用省略号截断。</summary>
    /// <remarks>
    /// 用**二分查找**而不是逐字符回退：测量走 GDI+ 的 <c>MeasureString</c>，
    /// 超长文件名（可达 260 字符）逐字符测会调用两百多次。
    /// 二分只要 <c>log2(n)</c> 次，且结果与线性回退完全一致（宽度单调不减）。
    /// </remarks>
    private string Ellipsize(string text, Font font, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (MeasureWidth(text, font) <= maxWidth) return text;

        var lo = 1;                 // 至少保留一个字符 + 省略号
        var hi = text.Length - 1;   // 不可能全部保留（上面已排除）
        var best = "…";

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var candidate = text[..mid] + "…";

            if (MeasureWidth(candidate, font) <= maxWidth)
            {
                best = candidate;   // 能放下，尝试更长
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;       // 放不下，缩短
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- 绘制

    /// <summary>
    /// 柔和投影：由外向内叠多层低透明度圆角矩形，最内层被不透明的卡片盖住，只留边缘的渐变。
    /// </summary>
    /// <remarks>
    /// 底部比顶部多扩 <c>bias</c> 像素 —— 模拟"光从上方来"，比四边等宽的投影自然得多。
    /// </remarks>
    private void DrawShadow(Graphics g)
    {
        var layers = Math.Max(4, P(ShadowPad));
        var radius = P(Radius);
        var bias = Math.Max(1, P(2));

        for (var i = layers; i >= 1; i--)
        {
            var t = 1.0 - (double)(i - 1) / layers;
            var alpha = (int)Math.Round(11 * t * t);
            if (alpha <= 0) continue;

            var topPad = Math.Max(0, i - bias);
            var r = Rectangle.FromLTRB(
                _cardRect.Left - i, _cardRect.Top - topPad,
                _cardRect.Right + i, _cardRect.Bottom + i);

            using var path = UiKit.RoundedRect(r, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    private void DrawCard(Graphics g)
    {
        var radius = P(Radius);

        // 卡片底：竖向的淡蓝渐变
        using (var path = UiKit.RoundedRect(_cardRect, radius))
        {
            using var bg = new LinearGradientBrush(
                _cardRect,
                _hoverCard ? CardHoverTop : CardGradientTop,
                _hoverCard ? CardHoverBottom : CardGradientBottom,
                LinearGradientMode.Vertical);

            g.FillPath(bg, path);

            using var border = new Pen(CardBorder);
            g.DrawPath(border, path);
        }

        // 左侧强调条：浅蓝 → 深蓝的竖向渐变（裁剪在卡片圆角内）
        using (var clip = UiKit.RoundedRect(_cardRect, radius))
        {
            g.SetClip(clip);

            var barRect = new Rectangle(_cardRect.X, _cardRect.Y, _accentWidth + radius, _cardRect.Height);
            using var brush = new LinearGradientBrush(
                barRect, BarGradientTop, BarGradientBottom, LinearGradientMode.Vertical);

            using var barPath = UiKit.RoundedRect(barRect, radius);
            g.FillPath(brush, barPath);
        }

        g.ResetClip();
    }

    private void DrawContent(Graphics g)
    {
        DrawSingleLine(g, _title, _titleFont, _titleRect, TextPrimary);

        // 程序名 + 写入了 + 大小：混排、底部对齐（不同字号）
        var x = _processRowRect.Left;
        var bottom = _processRowRect.Bottom;
        x = DrawSegment(g, _process, _emphasisFont, TextPrimary, x, bottom);
        x = DrawSegment(g, "  写入了  ", _bodyFont, TextMuted, x, bottom);
        x = DrawSegment(g, _size, _emphasisFont, AccentBlue, x, bottom);
        if (_sizeNote.Length > 0) DrawSegment(g, " " + _sizeNote, _metaFont, TextMuted, x, bottom);

        // 文件名（只到文件名，已在布局阶段按可用宽度截断过）
        var fileTextWidth = _folderAvailable
            ? _fileRowRect.Width - P(IconButtonSize) - P(8)
            : _fileRowRect.Width;
        DrawSingleLine(g, _fileName, _bodyFont,
            new Rectangle(_fileRowRect.Left, _fileRowRect.Top, fileTextWidth, _fileRowRect.Height), TextBody);

        if (_folderAvailable) DrawFolderIcon(g);

        DrawSingleLine(g, _metaLeft, _metaFont, _metaRowRect, TextMuted);
        DrawLink(g);
        DrawCloseIcon(g);
    }

    /// <summary>
    /// 单行文字绘制。
    /// </summary>
    /// <remarks>
    /// ★ 用 GDI+ 的 <see cref="Graphics.DrawString(string, Font, Brush, RectangleF, StringFormat)"/>
    /// 而不是 <c>TextRenderer.DrawText</c>：
    /// <b>GDI 的 ClearType 在带 alpha 通道的位图上会退化成「无抗锯齿」</b>，
    /// 文字变成又粗又硬的块状（实测对比过 7 种组合，只有 GDI+ 的 ClearType 能正常抗锯齿）。
    /// </remarks>
    private static void DrawSingleLine(Graphics g, string text, Font font, Rectangle rect, Color color)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, TypographicSingleLine);
    }

    /// <summary>画一段文字并返回下一个绘制起点（用于不同字号的混排）。</summary>
    private int DrawSegment(Graphics g, string text, Font font, Color color, int x, int bottom)
    {
        var width = MeasureWidth(text, font);
        var height = Measure(text, font, int.MaxValue).Height;

        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new Rectangle(x, bottom - height, width + 2, height), TypographicSingleLine);

        return x + width;
    }

    /// <summary>关闭按钮：矢量「×」+ 悬停时的淡灰圆角背景。</summary>
    private void DrawCloseIcon(Graphics g)
    {
        if (_hoverClose)
        {
            using var bg = new SolidBrush(HoverBack);
            using var p = UiKit.RoundedRect(_closeRect, P(6));
            g.FillPath(bg, p);
        }

        var c = _closeRect;
        var inset = (int)Math.Round(c.Width * 0.34);
        var color = _hoverClose ? TextPrimary : TextMuted;

        using var pen = new Pen(color, Math.Max(1.2f, 1.5f * _scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawLine(pen, c.Left + inset, c.Top + inset, c.Right - inset, c.Bottom - inset);
        g.DrawLine(pen, c.Right - inset, c.Top + inset, c.Left + inset, c.Bottom - inset);
    }

    /// <summary>「打开文件夹」按钮：矢量文件夹图标 + 悬停时的淡灰圆角背景。</summary>
    /// <remarks>
    /// 用经典文件夹轮廓的多边形（左上角有一个抬起的"文件夹盖"）而不是两个圆角矩形拼 ——
    /// 后者在 16 px 尺寸下会糊成一个方块，辨识度不足。
    /// </remarks>
    private void DrawFolderIcon(Graphics g)
    {
        if (_hoverFolder)
        {
            using var bg = new SolidBrush(HoverBack);
            using var p = UiKit.RoundedRect(_folderRect, P(6));
            g.FillPath(bg, p);
        }

        var color = _hoverFolder ? AccentBlue : TextMuted;
        using var brush = new SolidBrush(color);

        var size = (int)Math.Round(_folderRect.Width * 0.62);
        var x = _folderRect.Left + (_folderRect.Width - size) / 2f;
        var y = _folderRect.Top + (_folderRect.Height - size) / 2f;
        var u = size / 16f;   // 以 16×16 为设计基准

        // 经典文件夹轮廓：左下 → 左上 → 盖子右端 → 斜下 → 主体右上 → 右下
        var points = new[]
        {
            new PointF(x + 1.4f * u, y + 13.6f * u),
            new PointF(x + 1.4f * u, y + 3.4f * u),
            new PointF(x + 6.2f * u, y + 3.4f * u),
            new PointF(x + 8.0f * u, y + 5.9f * u),
            new PointF(x + 14.6f * u, y + 5.9f * u),
            new PointF(x + 14.6f * u, y + 13.6f * u),
        };

        g.FillPolygon(brush, points);
    }

    /// <summary>底部链接：悬停时变蓝 + 下划线。</summary>
    private void DrawLink(Graphics g)
    {
        var color = _hoverLink ? AccentBlue : TextMuted;

        using (var brush = new SolidBrush(color))
        {
            g.DrawString(_linkText, _metaFont, brush, _linkRect, TypographicRight);
        }

        if (!_hoverLink) return;

        var textH = Measure(_linkText, _metaFont, _linkRect.Width).Height;
        var underlineY = _linkRect.Top + (_linkRect.Height + textH) / 2 + 1;

        using var pen = new Pen(color, Math.Max(1f, _scale));
        g.DrawLine(pen, _linkRect.Left, underlineY, _linkRect.Right, underlineY);
    }

    // ---------------------------------------------------------------- 鼠标交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var overClose = _closeRect.Contains(e.Location);
        var overFolder = _folderAvailable && _folderRect.Contains(e.Location);
        var overLink = _linkRect.Contains(e.Location);
        var overCard = _cardRect.Contains(e.Location);

        if (overClose != _hoverClose || overFolder != _hoverFolder ||
            overLink != _hoverLink || overCard != _hoverCard)
        {
            _hoverClose = overClose;
            _hoverFolder = overFolder;
            _hoverLink = overLink;
            _hoverCard = overCard;

            Cursor = overCard ? Cursors.Hand : Cursors.Default;
            RenderSurface();
        }

        UpdateTooltip(e.Location, overFolder);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        _hoverCard = _hoverClose = _hoverFolder = _hoverLink = false;
        Cursor = Cursors.Default;
        HideTooltip();
        RenderSurface();
    }

    /// <summary>
    /// 悬停提示。主行只显示文件名，完整路径靠这里补全。
    /// </summary>
    private void UpdateTooltip(Point location, bool overFolder)
    {
        string? text = null;

        if (_closeRect.Contains(location)) text = "关闭";
        else if (overFolder) text = "在资源管理器中打开";
        else if (_fileRowRect.Contains(location)) text = _fullPath;

        if (string.IsNullOrEmpty(text))
        {
            HideTooltip();
            return;
        }

        if (_tipShown && text == _tipText) return;   // 同一区域内移动不要反复重弹

        _tip.Show(text, this, location.X + P(12), location.Y + P(22), 20_000);
        _tipText = text;
        _tipShown = true;
    }

    private void HideTooltip()
    {
        if (!_tipShown) return;
        _tip.Hide(this);
        _tipShown = false;
        _tipText = string.Empty;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        // 关闭按钮
        if (_closeRect.Contains(e.Location))
        {
            CloseNow();
            return;
        }

        // 「打开文件夹」按钮
        if (_folderAvailable && _folderRect.Contains(e.Location))
        {
            OpenInExplorer();
            CloseNow();
            return;
        }

        // 整卡可点：打开日志窗口（底部「单击查看日志」也落在这里，所以它同样有效）
        var openLog = OpenLogRequested;
        CloseNow();
        openLog?.Invoke();
    }

    /// <summary>在资源管理器里定位该文件；文件已不在则打开所在目录。</summary>
    private void OpenInExplorer()
    {
        var path = _record.Path;

        // 与日志窗口同一套防御：路径会被拼进 explorer 的命令行
        if (!PathUtil.IsShellSafePath(path)) return;

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && PathUtil.IsShellSafePath(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch
        {
            // 打不开就算了 —— 弹窗本身不能因此出错
        }
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnTick(object? sender, EventArgs e)
    {
        if (_closing) return;

        // 鼠标停在卡片上时暂停倒计时，方便看清内容
        if (_hoverCard || _hoverClose || _hoverFolder || _hoverLink) return;

        _remainingMs -= _tick.Interval;
        if (_remainingMs <= 0)
        {
            CloseNow();
            return;
        }

        // 最后 400 ms 淡出：只改整体透明度，不重画（分层窗口下 Form.Opacity 会与之冲突）
        if (_remainingMs < 400)
        {
            _alpha = (byte)Math.Clamp(_remainingMs * 255 / 400, 0, 255);
            Present(_alpha);
        }
    }

    /// <summary>
    /// 移动卡片并**重新提交分层画面**。
    /// </summary>
    /// <remarks>
    /// 分层窗口的位置由 <c>UpdateLayeredWindow</c> 的 <c>pptDst</c> 决定 ——
    /// 只改 <see cref="Control.Location"/> 会让"窗口位置"和"已提交的画面位置"不一致，
    /// 多张卡片堆叠时会错位。所以移动后必须重新 Present 一次。
    /// </remarks>
    private void MoveTo(int x, int y)
    {
        Location = new Point(x, y);
        Present(_alpha);
    }

    private void CloseNow()
    {
        if (_closing) return;
        _closing = true;

        try { _tick.Stop(); } catch { /* ignore */ }
        HideTooltip();

        Open.Remove(this);
        RepositionStack();

        try { Close(); } catch { /* ignore */ }
    }

    /// <summary>把剩下的卡片重新从右下角往上排。</summary>
    private static void RepositionStack()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var bottom = area.Bottom - ScreenMargin;

        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var p = Open[i];
            if (p.IsDisposed) continue;

            p.MoveTo(area.Right - p.Width - ScreenMargin, bottom - p.Height);
            bottom -= p.Height + StackGap;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        Open.Remove(this);
        RepositionStack();

        _tick.Dispose();
        _tip.Dispose();
        _measureGraphics?.Dispose();
        _measureBitmap?.Dispose();
        _titleFont?.Dispose();
        _emphasisFont?.Dispose();
        _bodyFont?.Dispose();
        _metaFont?.Dispose();
    }

    /// <summary>程序退出时清掉所有卡片。</summary>
    public static void CloseAll()
    {
        foreach (var p in Open.ToArray())
        {
            try { p.CloseNow(); } catch { /* ignore */ }
        }
        Open.Clear();
    }

/// <summary>
/// 分层窗口（per-pixel alpha）提交。
/// </summary>
/// <remarks>
/// <para>普通窗口只能用 <c>TransparencyKey</c>（1 位掩码）或 <c>Form.Opacity</c>（整窗等比），
/// 两者都无法做到「只有卡片外侧透明、投影仍保持半透明渐变」。
/// <c>UpdateLayeredWindow</c> 是唯一能满足的组合。</para>
/// <para>位图必须经 <c>CreateDIBSection</c> 建立 32bpp 顶朝下 DIB ——
/// <c>Bitmap.GetHbitmap()</c> 返回的是不含 alpha 的 DDB，投影会变成黑块。</para>
/// </remarks>
internal static class Layered
{
    public const int WS_EX_LAYERED = 0x00080000;

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;

    /// <summary>把带 alpha 的位图提交到窗口。<paramref name="alpha"/> 是整体不透明度。</summary>
    public static void Present(IntPtr hwnd, Bitmap surface, int x, int y, byte alpha)
    {
        var w = surface.Width;
        var h = surface.Height;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // 负数 = 顶朝下，和 Bitmap 的行序一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            hBitmap = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

            CopyPixels(surface, bits, w, h);

            var old = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { X = 0, Y = 0 };
            var dst = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);

            SelectObject(memDc, old);
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把 Bitmap 的像素逐行拷进 DIB（两边 stride 可能不同，不能整块 memcpy）。</summary>
    private static void CopyPixels(Bitmap surface, IntPtr dest, int w, int h)
    {
        var data = surface.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var rowBytes = w * 4;

            for (var y = 0; y < h; y++)
            {
                var srcRow = data.Scan0 + y * data.Stride;
                var dstRow = dest + y * rowBytes;

                unsafe { Buffer.MemoryCopy((void*)srcRow, (void*)dstRow, rowBytes, rowBytes); }
            }
        }
        finally
        {
            surface.UnlockBits(data);
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        int usage, out IntPtr ppvBits, IntPtr hSection, int offset);
}

}

using System.Reflection;
using CWriteWatcher.App.Services;
using Xunit;

namespace CWriteWatcher.UiSmoke;

/// <summary>
/// 自绘置顶提醒窗（<c>AlertPopup</c>）的布局与行为冒烟测试。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有这个类：</b><c>AlertPopup</c> 是完全自绘的顶层窗口
/// （没有子控件、没有布局引擎兜底），尺寸和文字位置全靠自己算。
/// 这类代码编译器和普通单元测试都覆盖不到，出问题只有肉眼能看见。</para>
/// <para>这里钉死三个真实踩过的坑：</para>
/// <list type="number">
///   <item><b>卡片外侧多出约 39 px 空白</b>：在 <c>OnHandleCreated</c> 里设 <c>ClientSize</c>
///         不可靠 —— 此时窗口非客户区尺寸还没定下来，<c>Form.SetClientSizeCore</c>
///         会用旧的差值反算，高度被多加 39 px（实测 114 → 153）。改到 <c>OnLoad</c> 后正常。</item>
///   <item><b>长文本撑破布局</b>：主行只显示文件名并做**单行省略号截断**，
///         所以超长文件名的卡片高度必须与普通文件名**完全一致**。</item>
///   <item><b>文字模糊</b>：<c>TextRenderer</c>(GDI) 的 ClearType 在带 alpha 的位图上
///         会退化成**无抗锯齿**（块状）。改为 GDI+ <c>DrawString</c> 后才正常。
///         另外 GDI+ 默认**不计行尾空格**，混排会贴在一起 —— 两个坑各有一条用例。</item>
/// </list>
/// <para>另外还覆盖：所有内容矩形都在卡片内、聚合提醒不提供「打开文件夹」按钮、
/// 底部链接与关闭按钮有可点击区域、不抢焦点且不占任务栏、渲染位图分辨率与窗口 DPI 一致。</para>
/// </remarks>
public class AlertPopupLayoutTests
{
    private static readonly object InitGate = new();
    private static bool _inited;

    private static void EnsureWinFormsInit()
    {
        lock (InitGate)
        {
            if (_inited) return;
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* 已初始化 */ }
            try { Application.EnableVisualStyles(); } catch { /* ignore */ }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { /* ignore */ }
            _inited = true;
        }
    }

    private static Type PopupType =>
        typeof(WatcherHost).Assembly.GetType("CWriteWatcher.App.Forms.AlertPopup", throwOnError: true)!;

    private static Type RecordType =>
        typeof(CWriteWatcher.Core.Models.AlertRecord);

    private static object MakeRecord(string path, long size, bool exact, string process, int aggregatedCount = 1)
    {
        var r = Activator.CreateInstance(RecordType)!;
        void Set(string name, object value) => RecordType.GetProperty(name)!.SetValue(r, value);

        Set("Timestamp", DateTimeOffset.Now);
        Set("Path", path);
        Set("Size", size);
        Set("SizeIsExact", exact);
        Set("ProcessId", 4242);
        Set("ProcessName", process);
        Set("TriggerMode", "B · 超过 10 MB");
        Set("CollectorMode", exact ? "精确模式" : "标准模式");
        Set("AggregatedCount", aggregatedCount);
        return r;
    }

    /// <summary>在 STA 线程上构造并 Show 一个真实的 AlertPopup（放到屏幕外，不打扰用户）。</summary>
    private static void WithPopup(string path, long size, bool exact, Action<Form, Type> check,
        int aggregatedCount = 1)
    {
        if (!Environment.UserInteractive) return; // 无桌面会话，跳过

        EnsureWinFormsInit();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var type = PopupType;
                var record = MakeRecord(path, size, exact, "chrome.exe", aggregatedCount);

                var ctor = type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    binder: null,
                    types: new[] { RecordType, typeof(int) },
                    modifiers: null)!;

                using var popup = (Form)ctor.Invoke(new object[] { record, 30 });

                // 放到屏幕外，避免测试时弹窗打扰；但必须真正 Show ——
                // 布局在 OnLoad 里做（OnHandleCreated 阶段设 ClientSize 不可靠）。
                popup.StartPosition = FormStartPosition.Manual;
                popup.Location = new Point(-4000, -4000);
                popup.Show();

                for (var i = 0; i < 5; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                check(popup, type);

                popup.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (error is not null)
        {
            throw new InvalidOperationException("置顶窗检查过程中抛出异常：" + error.Message, error);
        }
    }

    private static Rectangle Rect(Form popup, Type type, string field) =>
        (Rectangle)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!;

    [Fact]
    public void 卡片必须精确贴合内容_外侧只留对称的投影留白()
    {
        WithPopup(@"C:\Users\me\Downloads\setup.exe", 24L * 1024 * 1024, exact: true, (popup, type) =>
        {
            var card = Rect(popup, type, "_cardRect");

            var padLeft = card.Left;
            var padTop = card.Top;
            var padRight = popup.ClientSize.Width - card.Right;
            var padBottom = popup.ClientSize.Height - card.Bottom;

            // 卡片外侧只有「投影留白」，四边必须相等
            Assert.Equal(padLeft, padTop);
            Assert.Equal(padLeft, padRight);
            Assert.Equal(padLeft, padBottom);

            // 留白就是投影宽度（96 DPI 下 14 px，125% 下约 18 px）。
            // ★ 曾经因为「在 OnHandleCreated 里设 ClientSize」，高度会被多加约 39 px。
            Assert.True(padLeft is > 0 and <= 30,
                $"卡片外侧留白异常：{padLeft} px（应为投影宽度 14~18，过大说明 ClientSize 被多算了）");
        });
    }

    [Fact]
    public void 所有内容矩形都必须落在卡片内()
    {
        WithPopup(@"C:\Users\me\a.bin", 1024, exact: true, (popup, type) =>
        {
            var card = Rect(popup, type, "_cardRect");

            foreach (var field in new[]
                     {
                         "_titleRect", "_processRowRect", "_fileRowRect", "_metaRowRect",
                         "_closeRect", "_folderRect", "_linkRect",
                     })
            {
                var r = Rect(popup, type, field);

                Assert.True(r.Width > 0 && r.Height > 0, $"{field} 尺寸为 0：{r}");
                Assert.True(card.Contains(r), $"{field} 溢出卡片：{r}，卡片 {card}");
            }
        });
    }

    [Fact]
    public void 长路径必须只显示文件名()
    {
        const string deep = @"C:\Users\me\AppData\Local\Microsoft\Edge\User Data\Default\Cache\f_0000a1.bin";

        WithPopup(deep, 1024, exact: true, (popup, type) =>
        {
            var name = (string)type.GetField("_fileName", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(popup)!;

            Assert.Equal("f_0000a1.bin", name);
            Assert.DoesNotContain("\\", name);

            // 完整路径仍然保留（供 Tooltip 使用）
            var full = (string)type.GetField("_fullPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(popup)!;
            Assert.Equal(deep, full);
        });
    }

    [Fact]
    public void 超长文件名必须被省略号截断且不撑高卡片()
    {
        var longName = new string('x', 300) + ".bin";

        int? shortHeight = null;
        int? longHeight = null;
        string? shown = null;

        WithPopup(@"C:\a.bin", 1024, true, (popup, _) => shortHeight = popup.ClientSize.Height);

        WithPopup(@"C:\" + longName, 1024, true, (popup, type) =>
        {
            longHeight = popup.ClientSize.Height;
            shown = (string?)type.GetField("_fileName", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(popup);
        });

        Assert.NotNull(shortHeight);
        Assert.NotNull(longHeight);

        // 文件名是单行 + 省略号截断，所以**高度必须完全一致** —— 严禁长文本撑破布局
        Assert.Equal(shortHeight, longHeight);
        Assert.EndsWith("…", shown!);
    }

    [Fact]
    public void 底部链接与关闭按钮必须有可点击区域且在卡片内()
    {
        WithPopup(@"C:\a.bin", 1024, true, (popup, type) =>
        {
            var card = Rect(popup, type, "_cardRect");
            var link = Rect(popup, type, "_linkRect");
            var close = Rect(popup, type, "_closeRect");

            Assert.True(link.Width > 0 && link.Height > 0, $"底部链接没有可点击区域：{link}");
            Assert.True(card.Contains(link), $"底部链接必须在卡片内：{link} / {card}");
            Assert.True(card.Contains(close), $"关闭按钮必须在卡片内：{close} / {card}");
        });
    }

    [Fact]
    public void 聚合提醒不得提供打开文件夹按钮()
    {
        static bool FolderAvailable(Form popup, Type type) =>
            (bool)type.GetField("_folderAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(popup)!;

        // 普通单文件提醒：应当提供
        WithPopup(@"C:\a.bin", 1024, exact: true,
            (popup, type) => Assert.True(FolderAvailable(popup, type), "普通单文件提醒应当提供「打开文件夹」按钮"),
            aggregatedCount: 1);

        // 聚合提醒的 Path 是 "C:\a.bin 等 3 个文件"，不是真实路径，不能拿去打开资源管理器
        WithPopup(@"C:\a.bin 等 3 个文件", 1024, exact: true,
            (popup, type) => Assert.False(FolderAvailable(popup, type), "聚合提醒不得提供「打开文件夹」按钮"),
            aggregatedCount: 3);
    }

    [Fact]
    public void 置顶窗必须不抢焦点且不占任务栏()
    {
        WithPopup(@"C:\a.bin", 1024, true, (popup, type) =>
        {
            Assert.False(popup.ShowInTaskbar, "置顶窗不得出现在任务栏");
            Assert.True(popup.TopMost, "置顶窗必须置顶");
            Assert.Equal(FormBorderStyle.None, popup.FormBorderStyle);

            // WS_EX_NOACTIVATE 保证不抢焦点；WS_EX_TOOLWINDOW 保证不进 Alt-Tab。
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;

            var createParams = (CreateParams)typeof(Form)
                .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(popup)!;

            Assert.True((createParams.ExStyle & WS_EX_NOACTIVATE) != 0,
                "缺少 WS_EX_NOACTIVATE，弹窗会抢走用户当前窗口的焦点");
            Assert.True((createParams.ExStyle & WS_EX_TOOLWINDOW) != 0,
                "缺少 WS_EX_TOOLWINDOW，弹窗会出现在 Alt-Tab 列表里");
        });
    }

    // ---------------------------------------------------------------- 文字渲染（清晰度相关的两个坑）

    private const BindingFlags Nf = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object Invoke(Form popup, Type type, string method, params object[] args) =>
        type.GetMethod(method, Nf)!.Invoke(popup, args)!;

    /// <summary>
    /// ★ 测量必须把**行尾空格**计入宽度。
    /// </summary>
    /// <remarks>
    /// 混排那一行是靠 <c>"  写入了  "</c> 的空格制造间距的。
    /// GDI+ 的 <c>MeasureString</c> **默认不把行尾空格算进宽度**（除非显式指定
    /// <c>StringFormatFlags.MeasureTrailingSpaces</c>），
    /// 于是测量偏窄、推进的 x 偏小 → 相邻段会**贴在一起**
    /// （实测过："写入了" 与 "24 MB" 紧贴）。
    /// </remarks>
    [Fact]
    public void 文字测量必须把行尾空格计入宽度()
    {
        WithPopup(@"C:\a.bin", 1024, true, (popup, type) =>
        {
            var font = (Font)type.GetField("_bodyFont", Nf)!.GetValue(popup)!;

            var without = (int)Invoke(popup, type, "MeasureWidth", "写入", font);
            var with = (int)Invoke(popup, type, "MeasureWidth", "写入  ", font);

            Assert.True(with > without,
                $"行尾空格未计入宽度（\"写入\"={without}，\"写入  \"={with}）—— " +
                "混排时相邻文字会贴在一起。检查 StringFormatFlags.MeasureTrailingSpaces 是否被去掉。");
        });
    }

    /// <summary>
    /// ★ 渲染位图的分辨率必须与窗口 DPI 一致。
    /// </summary>
    /// <remarks>
    /// <para>这是**结构性守卫**，不是能靠改代码复现红灯的用例 —— 说实话：
    /// 在单显示器 + DPI 感知进程下，<c>new Bitmap(...)</c> 的默认分辨率本来就等于屏幕 DPI，
    /// 所以本机把 <c>SetResolution</c> 去掉这条用例**照样通过**（已实测确认）。
    /// 它真正能拦住的是**多显示器混合缩放**：那时 <c>new Bitmap</c> 取系统 DPI（主屏），
    /// 而 <c>DeviceDpi</c> 是窗口所在屏的 DPI，两者不等就会按错误 DPI 栅格化文字。</para>
    /// <para>同类的「文字必须清晰」本身无法用断言表达，所以这里退而求其次，
    /// 钉住**可量化的前置条件**：位图分辨率、位图尺寸。</para>
    /// </remarks>
    [Fact]
    public void 渲染位图分辨率必须与窗口DPI一致()
    {
        WithPopup(@"C:\a.bin", 1024, true, (popup, type) =>
        {
            var surface = (Bitmap)type.GetField("_surface", Nf)!.GetValue(popup)!;

            Assert.NotNull(surface);
            Assert.Equal(popup.ClientSize.Width, surface.Width);
            Assert.Equal(popup.ClientSize.Height, surface.Height);

            Assert.Equal(popup.DeviceDpi, (int)surface.HorizontalResolution);
            Assert.Equal(popup.DeviceDpi, (int)surface.VerticalResolution);

            // 测量用的 Graphics 必须与渲染位图同一个 DPI —— 否则「按 A 测量、按 B 绘制」，
            // 文字宽度对不上，布局会错位。
            var measure = (Graphics)type.GetField("_measureGraphics", Nf)!.GetValue(popup)!;
            Assert.Equal((int)measure.DpiX, (int)surface.HorizontalResolution);
            Assert.Equal((int)measure.DpiY, (int)surface.VerticalResolution);
        });
    }
}

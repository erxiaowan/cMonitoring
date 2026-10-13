using System.Windows.Forms;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Config;
using Xunit;

namespace CWriteWatcher.UiSmoke;

/// <summary>
/// 设置窗体的布局冒烟测试：在真实 DPI 下构造真实的 <c>SettingsForm</c>，检查三类
/// 「肉眼一眼能看出、但编译器和单元测试完全覆盖不到」的缺陷。
/// </summary>
/// <remarks>
/// <para>这三条断言各自对应一个真实发生过的缺陷：</para>
/// <list type="number">
///   <item><b>按钮文字被纵向裁切</b>：<c>Button.AutoSize</c> 默认是 <c>false</c>，
///         高度固定 23px；而 125% DPI 下「Microsoft YaHei UI 9pt」需要 30px。</item>
///   <item><b>说明标签为空</b>：<c>Hint()</c>/<c>Warn()</c> 辅助方法漏了 <c>Text = text</c>，
///         整个界面的提示与警告文字全部占位但不显示。</item>
///   <item><b>分组框装不下内容</b>：高度写死，DPI 放大或提示文字换行后内容被截断。</item>
/// </list>
/// <para>没有桌面会话（服务 / CI 无头）时整体跳过，不会误报。</para>
/// </remarks>
public class SettingsFormLayoutTests
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

    /// <summary>在 STA 线程上构造真实的 SettingsForm，把窗体交给检查委托。</summary>
    /// <param name="check">断言逻辑。</param>
    /// <param name="waitUntil">
    /// 可选的等待条件。在跑 <paramref name="check"/> 之前会持续抽消息直到它返回 true。
    /// <para><b>为什么需要它：</b>「运行统计」等标签由 1 秒计时器填充。如果只固定等待 1.5 秒，
    /// 整个解决方案并发跑测试（另一个测试项目占满 CPU）时会出现「有时够、有时不够」的偶发失败。
    /// 固定时长等待本身就是错的写法，必须改成等条件。</para>
    /// </param>
    private static void WithSettingsForm(Action<Form> check, Func<Form, bool>? waitUntil = null)
    {
        if (!Environment.UserInteractive) return; // 无桌面会话，跳过

        EnsureWinFormsInit();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            string cfgPath = Path.Combine(Path.GetTempPath(), $"cwt-uismoke-{Guid.NewGuid():N}.json");
            try
            {
                var store = new ConfigStore(cfgPath);
                using var host = new WatcherHost(store);

                var formType = typeof(WatcherHost).Assembly
                    .GetType("CWriteWatcher.App.Forms.SettingsForm", throwOnError: true)!;

                using var form = (Form)Activator.CreateInstance(formType, new object[] { host })!;

                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();

                // 先抽一段时间消息：让计时器有机会首次填充、布局引擎完成异步布局
                Pump(1500);

                if (waitUntil is not null)
                {
                    var deadline = Environment.TickCount64 + 10_000;
                    while (!waitUntil(form) && Environment.TickCount64 < deadline)
                    {
                        Pump(100);
                    }
                }

                check(form);
                form.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { /* ignore */ }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(90));

        if (error is not null)
        {
            throw new InvalidOperationException("布局检查过程中抛出异常：" + error.Message, error);
        }
    }

    /// <summary>在 STA 线程上抽指定时长的消息。</summary>
    private static void Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    // ---------------------------------------------------------------- 断言 1

    [Fact]
    public void 所有按钮的高度必须放得下文字()
    {
        var bad = new List<string>();

        WithSettingsForm(form =>
        {
            foreach (var b in Descendants(form).OfType<Button>())
            {
                var need = b.PreferredSize.Height;
                if (b.Height < need)
                {
                    bad.Add($"\"{b.Text}\"  Height={b.Height} < 需要 {need}  (AutoSize={b.AutoSize})");
                }
            }
        });

        Assert.True(bad.Count == 0,
            $"有 {bad.Count} 个按钮高度不足，中文会被纵向裁切（请用 Ui.Btn 创建按钮）：\n  " +
            string.Join("\n  ", bad));
    }

    // ---------------------------------------------------------------- 断言 2

    [Fact]
    public void 分组框内的说明标签不得为空()
    {
        var empty = new List<string>();

        WithSettingsForm(
            check: form =>
            {
                foreach (var gb in Descendants(form).OfType<GroupBox>())
                {
                    foreach (var lbl in Descendants(gb).OfType<Label>())
                    {
                        if (lbl.AutoSize && string.IsNullOrWhiteSpace(lbl.Text))
                        {
                            empty.Add($"GroupBox(\"{gb.Text}\") 内的空标签，占位 {lbl.Width}x{lbl.Height}");
                        }
                    }
                }
            },
            // 等到所有说明标签都有文字为止（正常情况下 1.5 秒内就满足；
            // 并发跑测试被抢 CPU 时会多等一会儿，而不是直接误报）
            waitUntil: form => AllGroupLabelsHaveText(form));

        Assert.True(empty.Count == 0,
            $"有 {empty.Count} 个说明标签没有文字（Hint/Warn 可能漏了 Text 赋值）：\n  " +
            string.Join("\n  ", empty));
    }

    /// <summary>所有 GroupBox 内的 AutoSize 标签是否都已填上文字。</summary>
    private static bool AllGroupLabelsHaveText(Control root) =>
        Descendants(root).OfType<GroupBox>()
            .SelectMany(Descendants)
            .OfType<Label>()
            .All(l => !l.AutoSize || !string.IsNullOrWhiteSpace(l.Text));

    // ---------------------------------------------------------------- 断言 3

    [Fact]
    public void 控件不得超出父容器的可显示范围()
    {
        var bad = new List<string>();

        WithSettingsForm(form =>
        {
            foreach (var tabs in Descendants(form).OfType<TabControl>())
            {
                for (var i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i;
                    Pump(300); // 等该页完成布局，而不是只 DoEvents 一次
                    CollectOverflow(tabs.TabPages[i], bad);
                }
            }
        });

        Assert.True(bad.Count == 0,
            $"有 {bad.Count} 处控件被父容器裁掉（固定高度容器装不下内容）：\n  " +
            string.Join("\n  ", bad));
    }

    // ---------------------------------------------------------------- 辅助

    private static void CollectOverflow(Control parent, List<string> bad)
    {
        // 可滚动容器允许子控件超出可视区
        var scrollable = parent is ScrollableControl sc && sc.AutoScroll;
        var cr = parent.ClientRectangle;

        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;

            if (!scrollable)
            {
                var b = c.Bounds;
                if (b.Bottom > cr.Height + 1 || b.Right > cr.Width + 1)
                {
                    bad.Add($"{Describe(parent)} 装不下 {Describe(c)}  " +
                            $"（子={b.Width}x{b.Height} @{b.Left},{b.Top}；父可显示={cr.Width}x{cr.Height}）");
                }
            }

            CollectOverflow(c, bad);
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static string Describe(Control c)
    {
        var text = c.Text;
        if (!string.IsNullOrEmpty(text) && text.Length > 20) text = text[..20] + "…";
        return string.IsNullOrEmpty(text) ? c.GetType().Name : $"{c.GetType().Name}(\"{text}\")";
    }
    // ---------------------------------------------------------------- 断言 4

    /// <summary>
    /// ★「运行统计」的每一行都必须完整显示，且"丢失事件"这一项必须在里面。
    /// </summary>
    /// <remarks>
    /// <para>统计文本是**运行时**按数据拼出来的（行数会随功能增加而变长），
    /// 而外层 <c>GroupBox</c> 的 AutoSize 是**构造时**算的 ——
    /// 一旦 AutoSize 没跟着长，最后几行就会被静默裁掉。</para>
    /// <para>「丢失事件」尤其不能丢：它非零代表日志不完整，
    /// 被裁掉就等于用户永远不知道自己在看残缺的数据。</para>
    /// </remarks>
    [Fact]
    public void 运行统计必须完整显示且包含丢失事件()
    {
        string? text = null;
        string? clipReason = null;

        WithSettingsForm(
            check: form =>
            {
                var field = form.GetType().GetField("_lblStats",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var lbl = (Label?)field?.GetValue(form);

                Assert.NotNull(lbl);
                text = lbl!.Text;

                // 纵向不得被裁切
                if (lbl.Height < lbl.PreferredHeight)
                {
                    clipReason = $"标签高度 {lbl.Height} < 需要 {lbl.PreferredHeight}";
                    return;
                }

                // 必须完整落在父容器（GroupBox）里 —— 否则说明 GroupBox 没跟着长
                var parent = lbl.Parent;
                while (parent is not null and not GroupBox) parent = parent.Parent;
                if (parent is GroupBox box)
                {
                    var inBox = box.RectangleToClient(lbl.Parent!.RectangleToScreen(lbl.Bounds));
                    if (inBox.Bottom > box.ClientSize.Height)
                    {
                        clipReason = $"标签底部 {inBox.Bottom} 超出分组框高度 {box.ClientSize.Height}";
                    }
                }
            },
            waitUntil: form =>
            {
                var field = form.GetType().GetField("_lblStats",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return ((Label?)field?.GetValue(form))?.Text.Contains("丢失事件") == true;
            });

        Assert.Null(clipReason);
        Assert.NotNull(text);
        Assert.Contains("丢失事件", text!);
    }

}

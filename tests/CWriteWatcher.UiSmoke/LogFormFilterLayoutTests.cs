using System.Reflection;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Config;
using Xunit;

namespace CWriteWatcher.UiSmoke;

/// <summary>
/// 日志窗体日期范围选择器的冒烟测试。
/// </summary>
/// <remarks>
/// <para><b>需求</b>：日期筛选**只到「天」**（显示 <c>2026-10-12</c>），
/// 不要显示到小时（<c>2026-10-12 09</c>）。</para>
/// <para><b>为什么专门测这个</b>：这里同时踩过两个坑 ——</para>
/// <list type="number">
///   <item>格式写成 <c>"yyyy-MM-dd HH:mm"</c> 后，在 150 px 宽 + 125% DPI 下
///         被裁成 <c>2026-10-12 09</c>，看着像坏掉的半截时间；</item>
///   <item><c>DateTimePicker.Width</c> 是**原始像素**，不随 DPI 放大，
///         所以宽度写死就会在高 DPI 下裁切。</item>
/// </list>
/// <para>另外：<c>DateTimePicker.PreferredSize</c> **不反映内容宽度**（只返回当前宽度），
/// 所以不能用「<c>PreferredSize &gt; Width</c>」这类通用检查发现裁切 ——
/// 必须自己用 <c>TextRenderer.MeasureText</c> 量。这也是本测试类存在的理由。</para>
/// </remarks>
public class LogFormFilterLayoutTests
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

    /// <summary>在 STA 线程上构造真实的 LogForm，把窗体交给检查委托。</summary>
    private static void WithLogForm(Action<Form, Type> check)
    {
        if (!Environment.UserInteractive) return; // 无桌面会话，跳过

        EnsureWinFormsInit();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            string cfgPath = Path.Combine(Path.GetTempPath(), $"cwt-logsmoke-{Guid.NewGuid():N}.json");
            try
            {
                var store = new ConfigStore(cfgPath);
                using var host = new WatcherHost(store);

                var formType = typeof(WatcherHost).Assembly
                    .GetType("CWriteWatcher.App.Forms.LogForm", throwOnError: true)!;

                using var form = (Form)Activator.CreateInstance(formType, new object[] { host })!;

                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();

                for (var i = 0; i < 5; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                check(form, formType);
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
            throw new InvalidOperationException("日志窗体检查过程中抛出异常：" + error.Message, error);
        }
    }

    private static DateTimePicker Picker(Form form, Type type, string field) =>
        (DateTimePicker)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    [Fact]
    public void 时间选择器必须只精确到天()
    {
        WithLogForm((form, type) =>
        {
            foreach (var field in new[] { "_dtFrom", "_dtTo" })
            {
                var picker = Picker(form, type, field);

                Assert.Equal(DateTimePickerFormat.Custom, picker.Format);
                Assert.Equal("yyyy-MM-dd", picker.CustomFormat);

                // ★ 不能带时分。曾经写成 "yyyy-MM-dd HH:mm"，在 150 px + 125% DPI 下
                //   被裁成「2026-10-12 09」—— 看着像坏掉的半截时间。
                Assert.DoesNotContain("HH", picker.CustomFormat);
                Assert.DoesNotContain("mm", picker.CustomFormat);
                Assert.DoesNotContain("ss", picker.CustomFormat);
            }
        });
    }

    [Fact]
    public void 时间选择器宽度必须放得下完整日期文本()
    {
        WithLogForm((form, type) =>
        {
            foreach (var field in new[] { "_dtFrom", "_dtTo" })
            {
                var picker = Picker(form, type, field);

                // 独立量一次：在窗体当前字体下，"yyyy-MM-dd" 最长形态需要多宽。
                var text = TextRenderer.MeasureText("2026-10-12", picker.Font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                // 复选框 + 下拉按钮 + 内边距的保守下限（96 DPI 逻辑值，按字体等比放大）。
                var chrome = (int)Math.Round(40 * (picker.Font.Height / 17.0));
                var required = text + chrome;

                Assert.True(picker.Width >= required,
                    $"{field} 宽度不足：实际 {picker.Width} px，至少需要 {required} px" +
                    $"（文本 {text} + 控件装饰 {chrome}，字体 {picker.Font.SizeInPoints}pt）。" +
                    "宽度写死会在高 DPI 下把日期裁掉。");
            }
        });
    }

    [Fact]
    public void 打开日志窗口时默认查询今天全天()
    {
        WithLogForm((form, type) =>
        {
            var buildQuery = type.GetMethod("BuildQuery", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var query = buildQuery.Invoke(form, null)!;
            var qt = query.GetType();

            var from = (DateTimeOffset?)qt.GetProperty("From")!.GetValue(query);
            var to = (DateTimeOffset?)qt.GetProperty("To")!.GetValue(query);

            Assert.NotNull(from);
            Assert.NotNull(to);

            // ★ 曾经有个隐蔽缺陷：DateTimePicker.Checked 默认就是 true，
            //   而 Value 默认是 DateTime.Now —— 查询区间变成「From = 现在、To = 现在」，
            //   打开日志窗口**什么都查不到**，必须点一次「重置条件」才正常。
            //   现在默认区间是「今天全天」，两端都落在今天。
            Assert.Equal(DateTime.Today, from!.Value.LocalDateTime.Date);
            Assert.Equal(DateTime.Today, to!.Value.LocalDateTime.Date);

            Assert.Equal(TimeSpan.Zero, from.Value.LocalDateTime.TimeOfDay);
            Assert.True(to.Value.LocalDateTime.TimeOfDay > TimeSpan.FromHours(23),
                $"结束时间应当覆盖到当天最后一刻，实际 {to.Value.LocalDateTime:HH:mm:ss}");
        });
    }

    [Fact]
    public void 必须提供按天的常用区间快捷按钮()
    {
        WithLogForm((form, type) =>
        {
            var texts = Descendants(form)
                .OfType<Button>()
                .Select(b => b.Text)
                .ToList();

            // 日期选择器只到「天」，快捷按钮也必须是天为单位 ——
            // 否则会出现「按钮说最近 1 小时、但选择器显示今天」这种对不上的情况。
            foreach (var expected in new[] { "今天", "昨天", "最近 7 天", "全部时间" })
            {
                Assert.True(texts.Contains(expected),
                    $"缺少快捷区间按钮「{expected}」，实际按钮：{string.Join(" / ", texts)}");
            }

            Assert.DoesNotContain("最近 1 小时", texts);
            Assert.DoesNotContain("最近 24 小时", texts);
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}

using System.Reflection;
using System.Windows.Forms;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Models;
using Xunit;

namespace CWriteWatcher.UiSmoke;

/// <summary>
/// 「按天统计」窗口（<c>TrendForm</c>）的布局冒烟测试。
/// </summary>
/// <remarks>
/// <para>钉死一个真实踩过的坑：<b>「峰值对比」列被省略号截断</b>。
/// 这一列里最长的一行是"满格 + 100%"（也就是**写入量最大的那一天**），
/// 而它恰恰是用户打开这个窗口要找的答案。列宽不够时 DataGridView 会静默加省略号 ——
/// 不报错、不换行，只是把最关键的信息藏起来。</para>
/// <para>修法是按实际字体量出最宽内容作为列的最小宽度（见 <c>TrendForm</c> 构造），
/// 这里反过来断言它成立。</para>
/// </remarks>
public class TrendFormLayoutTests
{
    private static Type FormType =>
        typeof(WatcherHost).Assembly.GetType("CWriteWatcher.App.Forms.TrendForm", throwOnError: true)!;

    /// <summary>在 STA 线程上构造并 Show 一个真实的 TrendForm（放到屏幕外）。</summary>
    private static void WithTrend(
        string scope, IReadOnlyList<(int DaysAgo, long Bytes, long Count)> days, Action<Form, DataGridView> check)
    {
        if (!Environment.UserInteractive) return; // 无桌面会话，跳过

        EnsureWinFormsInit();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var today = DateTime.Today;
                var listType = typeof(List<>).MakeGenericType(typeof(DailyWriteTotal));
                var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                foreach (var (ago, bytes, count) in days)
                {
                    list.Add(new DailyWriteTotal(today.AddDays(-ago), count, bytes, true));
                }

                using var form = (Form)Activator.CreateInstance(
                    FormType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { scope, list },
                    culture: null)!;

                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();

                for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }

                var grid = Descendants(form).OfType<DataGridView>().Single();
                check(form, grid);

                form.Close();
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
            throw new InvalidOperationException("趋势窗检查过程中抛出异常：" + error.Message, error);
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

    private static void EnsureWinFormsInit()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* 已初始化 */ }
        try { Application.EnableVisualStyles(); } catch { /* ignore */ }
        try { Application.SetCompatibleTextRenderingDefault(false); } catch { /* ignore */ }
    }

    /// <summary>
    /// ★ 在**窗口最窄时**，「峰值对比」列也必须放得下最宽的内容。
    /// </summary>
    /// <remarks>
    /// <para>最宽的内容就是写入量最大的那一天（满格 + 100%）。一旦列宽不足，
    /// DataGridView 会静默加省略号 —— 而那一行正是用户打开这个窗口要找的答案。</para>
    /// <para><b>必须在最小尺寸下断言</b>：在默认尺寸下测是不够的 ——
    /// 默认窗口更宽，问题不会暴露；用户把窗口拖窄（或系统 DPI 更高）才会被截断。
    /// 第一次写这条用例时就是在默认尺寸下断言的，把修复去掉照样通过，等于没测。</para>
    /// </remarks>
    [Fact]
    public void 峰值对比列在窗口最窄时也必须放得下最宽的内容()
    {
        // 刻意让峰值很大：满格那一行就是会被截断的那一行
        WithTrend("2026-10-07 ~ 2026-10-13", new[]
        {
            (0, 3_400_000_000L, 812L),
            (1, 1_200_000_000L, 430L),
            (3, 58_000_000_000L, 1402L),   // 峰值
            (4, 12_000_000L, 7L),
        }, (form, grid) =>
        {
            // 收到最窄允许尺寸 —— 用户把窗口拖到最小就是这个状态
            form.Size = form.MinimumSize;
            for (var i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(20); }

            var col = grid.Columns[grid.Columns.Count - 1];
            Assert.Equal("峰值对比", col.HeaderText);

            foreach (DataGridViewRow row in grid.Rows)
            {
                var text = row.Cells[col.Index].Value?.ToString() ?? string.Empty;
                var need = TextRenderer.MeasureText(text, grid.Font).Width;

                Assert.True(col.Width >= need,
                    $"窗口最窄时「峰值对比」列被截断：列宽 {col.Width} < 需要 {need}（内容 \"{text}\"）。" +
                    "最长的一行就是写入量最大的那一天，被省略号截掉等于把答案藏起来。");
            }
        });
    }

    /// <summary>零字节必须显示成破折号，不能画一格 —— 否则看起来像"有一点写入"。</summary>
    [Fact]
    public void 零字节必须显示成破折号而不是空格子()
    {
        WithTrend("2026-10-13", new[]
        {
            (0, 1_000_000L, 5L),
            (1, 0L, 0L),          // 零字节
        }, (_, grid) =>
        {
            var zeroRow = grid.Rows.Cast<DataGridViewRow>()
                .Single(r => (r.Cells[3].Value?.ToString() ?? string.Empty).StartsWith("0 B"));

            var bar = zeroRow.Cells[4].Value?.ToString() ?? string.Empty;

            Assert.Equal("—", bar);
            Assert.DoesNotContain("█", bar);
        });
    }

    /// <summary>表头必须写明范围与合计 —— 不然用户不知道这个数字是哪个范围的。</summary>
    [Fact]
    public void 表头必须写明范围与合计()
    {
        WithTrend("2026-10-13", new[] { (0, 1_500_000_000L, 100L) }, (form, _) =>
        {
            var labels = Descendants(form).OfType<Label>().Select(l => l.Text).ToList();
            var head = labels.FirstOrDefault(t => t.Contains("合计"));

            Assert.NotNull(head);
            Assert.Contains("2026-10-13", head!);
            Assert.Contains("合计", head!);
            Assert.Contains("1.4 GB", head!);   // 1.5e9 字节 ≈ 1.4 GB
        });
    }
}

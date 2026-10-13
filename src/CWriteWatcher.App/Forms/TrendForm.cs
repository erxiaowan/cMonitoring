using CWriteWatcher.Core.Models;

namespace CWriteWatcher.App.Forms;

/// <summary>
/// 按天统计（趋势）窗口：把一段时间内的写入量按**本地日期**汇总。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：日志表格是「逐条事件」，回答不了「我这周到底往 C 盘写了多少」。
/// 而这个问题恰恰是用户最关心的 —— 它直接对应 SSD 的写入寿命。</para>
/// <para><b>为什么不做图表</b>：一列用方块字符拼的占比条已经足够看出量级差异，
/// 而且不引入任何绘图代码与第三方依赖。表格本身就是精确数据。</para>
/// </remarks>
internal sealed class TrendForm : Form
{
    private readonly Label _lblHead = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
    };

    private readonly Label _lblNote = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A),
    };

    private readonly DataGridView _grid = new();

    public TrendForm(string scopeText, IReadOnlyList<DailyWriteTotal> days)
    {
        Text = "按天统计";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowInTaskbar = false;
        MinimizeBox = false;

        var totalBytes = days.Sum(d => d.TotalBytes);
        var totalCount = days.Sum(d => d.Count);
        var allExact = days.Count > 0 && days.All(d => d.AllExact);

        _lblHead.Text =
            $"{scopeText} 合计 {FileWriteEvent.FormatSize(totalBytes, allExact)}（{totalCount:N0} 条）";

        _lblNote.Text = days.Count == 0
            ? "该范围内没有记录。"
            : $"共 {days.Count} 天有记录 · 平均每天 {FileWriteEvent.FormatSize(totalCount == 0 ? 0 : totalBytes / days.Count, allExact)}";

        // ---- 表格 ----
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "日期", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "星期", Width = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "条数", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入量", Width = 110 });

        var pctColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "峰值对比",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        };
        _grid.Columns.Add(pctColumn);

        // ★ 给占比列设一个"最宽内容"的下限宽度。
        //   不设的话，窗口窄一点或 DPI 高一点，最长的那一行（满格 + 100.0%）
        //   就会被省略号截断 —— 而它恰恰是"哪一天最重"的答案。
        //   用 MeasureText 按实际字体量，所以随 DPI 自动成立。
        var widest = new string('█', BarCells) + "  100.0%";
        pctColumn.MinimumWidth = TextRenderer.MeasureText(widest, _grid.Font).Width + 24;

        var max = days.Count == 0 ? 0 : days.Max(d => d.TotalBytes);

        foreach (var d in days)
        {
            _grid.Rows.Add(
                d.DisplayDate,
                d.DisplayWeekday,
                d.Count.ToString("N0"),
                d.DisplayTotal,
                BuildBar(d.TotalBytes, max));
        }

        // 窗口最小宽度要放得下所有列，否则又会出现截断
        var columnsWidth = 110 + 55 + 80 + 110 + pctColumn.MinimumWidth;
        MinimumSize = new Size(columnsWidth + 24 + 40, 420);
        Size = new Size(Math.Max(640, MinimumSize.Width), 520);

        // ---- 布局 ----
        var head = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 6),
        };
        head.Controls.Add(_lblHead);
        head.Controls.Add(_lblNote);

        var btnClose = Ui.Btn("关闭", 90);
        btnClose.Click += (_, _) => Close();

        var bottom = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 6, 0, 0),
        };
        bottom.Controls.Add(btnClose);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(head, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(bottom, 0, 2);

        Controls.Add(root);
        CancelButton = btnClose;
    }

    /// <summary>占比条的满格格数。太长会在窄窗口里被截断，14 格足够看出量级差异。</summary>
    private const int BarCells = 14;

    /// <summary>
    /// 用方块字符拼一条对比条（长度与百分比**都相对峰值**）。
    /// </summary>
    /// <remarks>
    /// <para>条长与数字必须同口径 —— 一个"条长相对峰值、数字相对合计"的列
    /// 会让用户对不上（条子看着长、百分比却小）。绝对量已经在「写入量」列里了，
    /// 这一列只回答"哪天最重"。</para>
    /// <para>零字节显示成 <c>—</c> 而不是一个空格子：给 0 画一格会让人以为"有一点写入"。</para>
    /// </remarks>
    private static string BuildBar(long value, long max)
    {
        if (value <= 0) return "—";
        if (max <= 0) return string.Empty;

        var cells = Math.Clamp((int)Math.Round(value * (double)BarCells / max), 1, BarCells);
        var percent = value * 100.0 / max;

        return new string('█', cells) + $"  {percent:0.#}%";
    }
}

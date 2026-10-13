using System.Diagnostics;
using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Logging;
using CWriteWatcher.Core.Util;

namespace CWriteWatcher.App.Forms;

/// <summary>日志查看 / 筛选 / 清空 / 导出（需求 §8）。</summary>
internal sealed class LogForm : Form
{
    private readonly WatcherHost _host;

    private readonly DataGridView _grid = new();

    // 时间范围选择器：**只到「天」**。
    //   CustomFormat = "yyyy-MM-dd"  —— 显示 2026-10-12，不带时分。
    //       曾经写成 "yyyy-MM-dd HH:mm"，在 150 px 宽 + 125% DPI 下被裁成
    //       「2026-10-12 09」—— 看着像坏掉的半截时间，用户明确要求改成只到天。
    //   ShowUpDown = false  —— 只到天时，日历下拉是最自然的选日期方式。
    //   Width 由 TimePickerWidth() 按字体算，不写死（写死会在高 DPI 下裁切）。
    private readonly DateTimePicker _dtFrom = CreateTimePicker();
    private readonly DateTimePicker _dtTo = CreateTimePicker();
    private readonly ToolTip _tip = new();

    private readonly TextBox _txtProcess = new() { Width = 130 };
    private readonly TextBox _txtPath = new() { Width = 200 };
    private readonly NumericUpDown _numMinMb = new() { Minimum = 0, Maximum = 1_000_000, Width = 90 };
    private readonly CheckBox _chkNotifiedOnly = new() { Text = "只看已提醒", AutoSize = true };
    private readonly Label _lblPage = new() { AutoSize = true, Text = "第 1 页" };
    private readonly Label _lblSummary = new() { AutoSize = true, ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A) };

    private static DateTimePicker CreateTimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",   // 只到「天」
        ShowCheckBox = true,
        ShowUpDown = false,            // 日历下拉
    };

    /// <summary>按当前字体算出日期选择器需要的宽度（复选框 + 下拉按钮 + 完整日期文本）。</summary>
    private static int TimePickerWidth(Font font)
    {
        var text = TextRenderer.MeasureText("2026-10-12", font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

        // 复选框 + 下拉按钮 + 左右内边距，按字体高度等比放大（9pt/96DPI 时 Font.Height = 17）。
        var chrome = (int)Math.Round(60 * (font.Height / 17.0));
        return text + chrome;
    }

    private int _pageIndex;
    private const int PageSize = 300;

    public LogForm(WatcherHost host)
    {
        _host = host;

        Text = "CWriteWatcher · 日志";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 660);
        MinimumSize = new Size(900, 480);
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildUi();
        _ = RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 筛选栏 ----
        var filter = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 6),
        };

        var btnQuery = Ui.Btn("查询", 80);
        var btnReset = Ui.Btn("重置条件", 90);
        var btnClear = Ui.Btn("清空日志", 90);
        var btnCsv = Ui.Btn("导出 CSV", 90);
        var btnJson = Ui.Btn("导出 JSON", 95);
        var btnPrev = Ui.Btn("上一页", 80);
        var btnNext = Ui.Btn("下一页", 80);

        // 时间选择器的宽度必须按字体算：写死宽度在 125% DPI 下会把日期文本裁掉。
        var pickerWidth = TimePickerWidth(Font);
        _dtFrom.Width = pickerWidth;
        _dtTo.Width = pickerWidth;

        // 默认区间 = **今天**（显式设置，不要依赖 DateTimePicker.Checked 的默认值 true）。
        //
        // 这里曾经有个隐蔽缺陷：Checked 默认就是 true，而 Value 默认是 DateTime.Now，
        // 于是查询区间变成「From = 现在、To = 现在」—— 打开日志窗口**什么都查不到**，
        // 必须点一次「重置条件」才正常。
        // 现在把两端都归到「今天」，语义就变成"今天全天"，符合直觉。
        _dtFrom.Value = DateTime.Today;
        _dtTo.Value = DateTime.Today;
        _dtFrom.Checked = true;
        _dtTo.Checked = true;

        _tip.SetToolTip(_dtFrom, "按「天」筛选：从该日 00:00:00 起");
        _tip.SetToolTip(_dtTo, "按「天」筛选：到该日 23:59:59 止（含全天）");
        _tip.SetToolTip(_chkNotifiedOnly, "只显示实际弹出了提醒的记录；被静默/节流压住的记录会被隐藏");

        btnQuery.Click += (_, _) => { _pageIndex = 0; _ = RefreshAsync(); };
        btnReset.Click += (_, _) => ResetFilters();
        btnClear.Click += (_, _) => _ = ClearAsync();
        btnCsv.Click += (_, _) => _ = ExportAsync(csv: true);
        btnJson.Click += (_, _) => _ = ExportAsync(csv: false);
        btnPrev.Click += (_, _) => { if (_pageIndex > 0) { _pageIndex--; _ = RefreshAsync(); } };
        btnNext.Click += (_, _) => { _pageIndex++; _ = RefreshAsync(); };

        // 常用日期区间一键设置。日期选择器只到「天」，所以快捷按钮也用天为单位，语义一致。
        var btnToday = Ui.Btn("今天", 65);
        var btnYesterday = Ui.Btn("昨天", 65);
        var btnLast7 = Ui.Btn("最近 7 天", 90);
        var btnAllTime = Ui.Btn("全部时间", 85);
        var btnTrend = Ui.Btn("按天统计", 90);

        btnToday.Click += (_, _) => SetDayRange(0, 0);
        btnYesterday.Click += (_, _) => SetDayRange(1, 1);
        btnLast7.Click += (_, _) => SetDayRange(6, 0);
        btnAllTime.Click += (_, _) => ClearTimeRange();
        btnTrend.Click += (_, _) => _ = ShowTrendAsync();

        filter.Controls.AddRange(new Control[]
        {
            new Label { Text = "从", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _dtFrom,
            new Label { Text = "到", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, _dtTo,
            btnToday, btnYesterday, btnLast7, btnAllTime, btnTrend,
            new Label { Text = "进程", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, _txtProcess,
            new Label { Text = "路径", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, _txtPath,
            new Label { Text = "最小", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, _numMinMb,
            new Label { Text = "MB", AutoSize = true, Margin = new Padding(0, 6, 4, 0) },
            _chkNotifiedOnly,
            btnQuery, btnReset,
            btnClear, btnCsv, btnJson,
            btnPrev, _lblPage, btnNext,
        });

        // ---- 表格 ----
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件路径", Width = 330 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入大小", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "精度", Width = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "进程", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PID", Width = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "触发模式", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "判定口径", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "已提醒", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var path = _grid.Rows[e.RowIndex].Cells[1].Value?.ToString();

            // ★ 防御性校验：路径来自日志（可被攻击者影响的文件名/进程名），
            //   而它会被拼进 `explorer.exe /select,"<path>"` 的命令行 ——
            //   含引号就能破坏引号配对注入参数，UNC 路径还会触发一次 SMB 连接。
            if (!PathUtil.IsShellSafePath(path)) return;

            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };

        root.Controls.Add(filter, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(_lblSummary, 0, 2);
        Controls.Add(root);
    }

    private void ResetFilters()
    {
        _dtFrom.Value = DateTime.Today;
        _dtTo.Value = DateTime.Today;
        _dtFrom.Checked = false;
        _dtTo.Checked = false;
        _txtProcess.Clear();
        _txtPath.Clear();
        _numMinMb.Value = 0;
        _chkNotifiedOnly.Checked = false;
        _pageIndex = 0;
        _ = RefreshAsync();
    }

    /// <summary>
    /// 一键设置常用日期区间并立即查询。
    /// </summary>
    /// <param name="fromDaysAgo">起始日期距今天的天数（0 = 今天）。</param>
    /// <param name="toDaysAgo">结束日期距今天的天数（0 = 今天）。</param>
    /// <remarks>
    /// 日期选择器只到「天」，所以这里也用天为单位 —— 语义一致，用户不会看到
    /// 「按钮说最近 1 小时、但选择器显示今天」这种对不上的情况。
    /// </remarks>
    private void SetDayRange(int fromDaysAgo, int toDaysAgo)
    {
        var today = DateTime.Today;
        _dtFrom.Value = today.AddDays(-fromDaysAgo);
        _dtTo.Value = today.AddDays(-toDaysAgo);
        _dtFrom.Checked = true;
        _dtTo.Checked = true;

        _pageIndex = 0;
        _ = RefreshAsync();
    }

    /// <summary>清除日期筛选（= 全部时间）。</summary>
    private void ClearTimeRange()
    {
        _dtFrom.Checked = false;
        _dtTo.Checked = false;

        _pageIndex = 0;
        _ = RefreshAsync();
    }

    private LogQuery BuildQuery()
    {
        var q = new LogQuery
        {
            Limit = PageSize,
            Offset = _pageIndex * PageSize,
        };

        // 选择器只到「天」，所以按整日展开：
        //   起始 = 该日 00:00:00
        //   结束 = 该日 23:59:59.9999999（含全天）
        // 「从 2026-10-12 到 2026-10-12」即"10 月 12 日全天"。
        if (_dtFrom.Checked) q.From = new DateTimeOffset(_dtFrom.Value.Date);
        if (_dtTo.Checked) q.To = new DateTimeOffset(_dtTo.Value.Date.AddDays(1).AddTicks(-1));

        if (!string.IsNullOrWhiteSpace(_txtProcess.Text)) q.ProcessFilter = _txtProcess.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtPath.Text)) q.PathFilter = _txtPath.Text.Trim();
        if (_numMinMb.Value > 0) q.MinSize = (long)(_numMinMb.Value * 1024 * 1024);
        if (_chkNotifiedOnly.Checked) q.NotifiedOnly = true;

        return q;
    }

    private async Task RefreshAsync()
    {
        var store = _host.LogStore;
        if (store is null) return;

        try
        {
            var query = BuildQuery();

            // 三个查询互不依赖，并行跑（各自独立连接）。
            var rowsTask = store.QueryAsync(query);
            var summaryTask = store.SummarizeAsync(query);
            var totalTask = store.CountAsync();
            await Task.WhenAll(rowsTask, summaryTask, totalTask).ConfigureAwait(true);

            var rows = rowsTask.Result;
            var summary = summaryTask.Result;
            var total = totalTask.Result;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var r in rows)
            {
                _grid.Rows.Add(
                    r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.DisplayPath,
                    r.DisplaySize,
                    r.SizeIsExact ? "精确" : "估算",
                    r.DisplayProcess,
                    r.ProcessId,
                    r.TriggerMode,
                    r.ThresholdScope,
                    r.Notified ? "是" : "否",
                    r.Note);
            }
            _grid.ResumeLayout();

            _lblPage.Text = $"第 {_pageIndex + 1} 页";

            // ★ 范围合计放在最前面 —— 这是用户最想知道的一个数（"我今天到底写了多少"）。
            //   它统计的是**整个筛选范围**，不是本页，所以必须写明范围。
            _lblSummary.Text =
                $"{DescribeScope(query)} 合计 {summary.DisplayTotal}（{summary.DisplayCount} 条）" +
                $" · 本页 {rows.Count} 条 · 数据库共 {total} 条 · 文件 {store.FileSize() / 1024.0 / 1024.0:0.#} MB" +
                $" · 已写入 {store.WrittenCount} 条 · 丢弃 {store.DroppedCount} 条" +
                $" · 数据文件：{store.DatabasePath}";
        }
        catch (Exception ex)
        {
            _lblSummary.Text = "查询失败：" + ex.Message;
        }
    }

    /// <summary>把当前筛选的时间范围描述成人话（合计数字必须说清它是哪个范围的）。</summary>
    private string DescribeScope(LogQuery q)
    {
        if (q.From is null && q.To is null) return "全部时间";

        var from = q.From?.ToLocalTime().Date;
        var to = q.To?.ToLocalTime().Date;

        if (from is not null && to is not null)
        {
            return from == to
                ? from.Value.ToString("yyyy-MM-dd")
                : $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";
        }

        return from is not null ? $"{from:yyyy-MM-dd} 起" : $"截至 {to:yyyy-MM-dd}";
    }

    /// <summary>打开「按天统计」窗口，范围与筛选条件一致。</summary>
    private async Task ShowTrendAsync()
    {
        var store = _host.LogStore;
        if (store is null) return;

        try
        {
            var query = BuildQuery();

            // 分页参数对聚合无意义，但其它筛选条件（进程/路径/最小/已提醒）要沿用 ——
            // 否则趋势窗口的数字会和列表对不上。
            var days = await store.DailyTotalsAsync(query).ConfigureAwait(true);

            using var dlg = new TrendForm(DescribeScope(query), days);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "统计失败：" + ex.Message, "按天统计",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ClearAsync()
    {
        if (_host.LogStore is null) return;

        var answer = MessageBox.Show(
            "将删除全部日志记录，此操作不可撤销。\n\n是否继续？",
            "清空日志", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes) return;

        await _host.LogStore.ClearAsync().ConfigureAwait(true);
        _pageIndex = 0;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ExportAsync(bool csv)
    {
        if (_host.LogStore is null) return;

        var ext = csv ? "csv" : "json";
        using var dlg = new SaveFileDialog
        {
            Filter = csv ? "CSV 文件 (*.csv)|*.csv" : "JSON 文件 (*.json)|*.json",
            FileName = $"CWriteWatcher-日志-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}",
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var query = BuildQuery();
            query.Offset = 0;

            var count = csv
                ? await _host.LogStore.ExportCsvAsync(dlg.FileName, query).ConfigureAwait(true)
                : await _host.LogStore.ExportJsonAsync(dlg.FileName, query).ConfigureAwait(true);

            MessageBox.Show($"已导出 {count} 条记录到：\n{dlg.FileName}", "导出完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "CWriteWatcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

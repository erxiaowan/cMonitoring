using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Util;
using Microsoft.Data.Sqlite;

namespace CWriteWatcher.Core.Logging;

/// <summary>
/// 事件日志存储（SQLite）。写入走独立的后台批量事务线程，绝不阻塞事件处理管道。
/// </summary>
/// <remarks>
/// 关键设计：
/// <list type="bullet">
///   <item>批量事务（每 200 条或 500 ms 提交一次），避免写放大。</item>
///   <item>WAL 模式，允许 UI 线程并发查询。</item>
///   <item>环形裁剪：按条数（默认 10 万）与体积（默认 200 MB）双上限。</item>
///   <item>内存超限时由宿主调用 <see cref="TrimAsync"/> 激进裁剪。</item>
/// </list>
/// </remarks>
public sealed class EventLogStore : IDisposable
{
    private readonly string _dbPath;
    private readonly Channel<AlertRecord> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _writer;
    private LoggingConfig _cfg;
    private readonly object _trimGate = new();

    public string DatabasePath => _dbPath;

    public long WrittenCount { get; private set; }
    public long DroppedCount { get; private set; }

    public EventLogStore(LoggingConfig config, string? dbPath = null)
    {
        _cfg = config;
        _dbPath = dbPath ?? PathUtil.DatabasePath;
        PathUtil.EnsureDirectory(Path.GetDirectoryName(_dbPath)!);

        _queue = Channel.CreateBounded<AlertRecord>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        Initialize();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Events (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Ts        INTEGER NOT NULL,
                Path      TEXT    NOT NULL,
                Size      INTEGER NOT NULL,
                SizeExact INTEGER NOT NULL,
                Pid       INTEGER NOT NULL,
                Process   TEXT    NOT NULL,
                Mode      TEXT    NOT NULL,
                Scope     TEXT    NOT NULL,
                Collector TEXT    NOT NULL,
                Notified  INTEGER NOT NULL,
                AggCount  INTEGER NOT NULL,
                Note      TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Events_Ts      ON Events(Ts DESC);
            CREATE INDEX IF NOT EXISTS IX_Events_Process ON Events(Process);
            CREATE INDEX IF NOT EXISTS IX_Events_Notified ON Events(Notified);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Start()
    {
        _writer ??= Task.Factory.StartNew(
            WriterLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>非阻塞入队。日志是"尽力而为"的，绝不因为写盘慢而拖累监控。</summary>
    public void Enqueue(AlertRecord record)
    {
        if (!_cfg.Enabled) return;
        if (!_cfg.LogSuppressed && !record.Notified) return;

        if (_queue.Reader.Count >= 49_000) DroppedCount++;
        _queue.Writer.TryWrite(record);
    }

    public void ApplyConfig(LoggingConfig config) => _cfg = config;

    private void WriterLoop()
    {
        var reader = _queue.Reader;
        var batch = new List<AlertRecord>(256);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                batch.Clear();

                // 阻塞等第一条
                if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) break;
                while (batch.Count < 200 && reader.TryRead(out var r)) batch.Add(r);

                if (batch.Count == 0) continue;

                // 再等最多 500ms 收集更多，凑成一批
                var deadline = Environment.TickCount64 + 500;
                while (batch.Count < 200 && Environment.TickCount64 < deadline)
                {
                    if (reader.TryRead(out var more)) batch.Add(more);
                    else Thread.Sleep(20);
                }

                try
                {
                    WriteBatch(batch);
                    WrittenCount += batch.Count;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LogStore] 批量写入失败：{ex.Message}");
                }

                if (_cfg.AutoTrim) TrimIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogStore] 写入线程退出：{ex}");
        }
    }

    private void WriteBatch(List<AlertRecord> batch)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO Events (Ts, Path, Size, SizeExact, Pid, Process, Mode, Scope, Collector, Notified, AggCount, Note)
            VALUES ($ts, $path, $size, $exact, $pid, $proc, $mode, $scope, $col, $notified, $agg, $note);
            """;

        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pExact = cmd.Parameters.Add("$exact", SqliteType.Integer);
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
        var pMode = cmd.Parameters.Add("$mode", SqliteType.Text);
        var pScope = cmd.Parameters.Add("$scope", SqliteType.Text);
        var pCol = cmd.Parameters.Add("$col", SqliteType.Text);
        var pNotified = cmd.Parameters.Add("$notified", SqliteType.Integer);
        var pAgg = cmd.Parameters.Add("$agg", SqliteType.Integer);
        var pNote = cmd.Parameters.Add("$note", SqliteType.Text);

        foreach (var r in batch)
        {
            pTs.Value = r.Timestamp.ToUnixTimeMilliseconds();
            pPath.Value = r.Path ?? string.Empty;
            pSize.Value = r.Size;
            pExact.Value = r.SizeIsExact ? 1 : 0;
            pPid.Value = r.ProcessId;
            pProc.Value = r.ProcessName ?? string.Empty;
            pMode.Value = r.TriggerMode ?? string.Empty;
            pScope.Value = r.ThresholdScope ?? string.Empty;
            pCol.Value = r.CollectorMode ?? string.Empty;
            pNotified.Value = r.Notified ? 1 : 0;
            pAgg.Value = r.AggregatedCount;
            pNote.Value = r.Note ?? string.Empty;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void TrimIfNeeded()
    {
        if (!Monitor.TryEnter(_trimGate)) return;
        try
        {
            var maxRows = _cfg.MaxRows;
            using var conn = Open();

            long count;
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM Events;";
                count = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
            }

            if (count > maxRows)
            {
                using var del = conn.CreateCommand();
                del.CommandText = """
                    DELETE FROM Events WHERE Id IN (
                        SELECT Id FROM Events ORDER BY Id ASC LIMIT $n
                    );
                    """;
                del.Parameters.AddWithValue("$n", count - maxRows);
                del.ExecuteNonQuery();
            }

            // 体积上限
            var maxBytes = (long)_cfg.MaxSizeMb * 1024 * 1024;
            var size = FileSize();
            if (size > maxBytes)
            {
                using var del = conn.CreateCommand();
                del.CommandText = """
                    DELETE FROM Events WHERE Id IN (
                        SELECT Id FROM Events ORDER BY Id ASC LIMIT $n
                    );
                    """;
                del.Parameters.AddWithValue("$n", Math.Max(1000, count / 4));
                del.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogStore] 裁剪失败：{ex.Message}");
        }
        finally
        {
            Monitor.Exit(_trimGate);
        }
    }

    /// <summary>由宿主在内存超限时调用的激进裁剪（v0.2 §6.5）。</summary>
    public Task TrimAsync(int keepRows) => Task.Run(() =>
    {
        lock (_trimGate)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM Events WHERE Id NOT IN (
                        SELECT Id FROM Events ORDER BY Id DESC LIMIT $n
                    );
                    """;
                cmd.Parameters.AddWithValue("$n", Math.Max(1000, keepRows));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogStore] 激进裁剪失败：{ex.Message}");
            }
        }
    });

    public long FileSize()
    {
        try
        {
            long total = 0;
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(f)) total += new FileInfo(f).Length;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public Task<long> CountAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Events;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    });

    /// <summary>分页查询的单页上限（UI 翻页用，防止一次拉太多进内存）。</summary>
    private const int MaxPageSize = 5_000;

    /// <summary>
    /// 导出时的行数上限。
    /// </summary>
    /// <remarks>
    /// ★ 必须与 <see cref="MaxPageSize"/> 分开：两者共用同一个钳制值时，
    /// 导出会被**静默截断**到 5000 条 —— 而且提示还会说"已导出 5000 条"，用户以为导全了。
    /// 本项目的日志保留上限默认 10 万条，所以这里给到 20 万足够覆盖。
    /// </remarks>
    private const int MaxExportRows = 200_000;

    public Task<List<AlertRecord>> QueryAsync(LogQuery q) =>
        QueryCoreAsync(q, Math.Clamp(q.Limit, 1, MaxPageSize));

    /// <summary>导出专用：忽略 <c>q.Limit</c>，用导出上限。</summary>
    private Task<List<AlertRecord>> QueryForExportAsync(LogQuery q) =>
        QueryCoreAsync(q, MaxExportRows);

    private Task<List<AlertRecord>> QueryCoreAsync(LogQuery q, int limit) => Task.Run(() =>
    {
        var list = new List<AlertRecord>(Math.Min(limit, 4096));
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var whereSql = BuildWhere(q, cmd);

        cmd.CommandText = $"""
            SELECT Id, Ts, Path, Size, SizeExact, Pid, Process, Mode, Scope, Collector, Notified, AggCount, Note
            FROM Events {whereSql}
            ORDER BY Id DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, q.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AlertRecord
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                Path = reader.GetString(2),
                Size = reader.GetInt64(3),
                SizeIsExact = reader.GetInt64(4) != 0,
                ProcessId = (int)reader.GetInt64(5),
                ProcessName = reader.GetString(6),
                TriggerMode = reader.GetString(7),
                ThresholdScope = reader.GetString(8),
                CollectorMode = reader.GetString(9),
                Notified = reader.GetInt64(10) != 0,
                AggregatedCount = (int)reader.GetInt64(11),
                Note = reader.GetString(12),
            });
        }

        return list;
    });

    /// <summary>
    /// 按 <see cref="LogQuery"/> 的筛选条件拼 WHERE 子句（并把参数加到 <paramref name="cmd"/>）。
    /// </summary>
    /// <remarks>
    /// ★ 分页查询与聚合查询**必须共用这一个方法**。
    /// 两边各写一份的话，一旦筛选条件改动而只改了一处，
    /// 就会出现「列表里是 100 条、合计却是另一个范围的数字」这种对不上的情况 ——
    /// 而这种不一致很难被发现，因为两个数字单看都"合理"。
    /// </remarks>
    private static string BuildWhere(LogQuery q, SqliteCommand cmd)
    {
        var where = new List<string>();
        if (q.From is { } from) { where.Add("Ts >= $from"); cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds()); }
        if (q.To is { } to) { where.Add("Ts <= $to"); cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds()); }
        if (!string.IsNullOrWhiteSpace(q.ProcessFilter)) { where.Add("Process LIKE $proc"); cmd.Parameters.AddWithValue("$proc", $"%{q.ProcessFilter}%"); }
        if (!string.IsNullOrWhiteSpace(q.PathFilter)) { where.Add("Path LIKE $path"); cmd.Parameters.AddWithValue("$path", $"%{q.PathFilter}%"); }
        if (q.MinSize is { } min) { where.Add("Size >= $min"); cmd.Parameters.AddWithValue("$min", min); }
        if (q.NotifiedOnly is { } n) { where.Add("Notified = $n"); cmd.Parameters.AddWithValue("$n", n ? 1 : 0); }

        return where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
    }

    // ---------------------------------------------------------------- 聚合（趋势）

    /// <summary>
    /// 统计符合筛选条件的写入合计。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么 <c>SUM(Size)</c> 是对的</b>：<c>Size</c> 存的是**累计值** ——
    /// 聚合提醒产生的行里，<c>Size</c> 已经是该组所有写入之和（见 <c>AlertAggregator</c>），
    /// 所以直接求和即可，<b>不能再乘 <c>AggCount</c></b>（那会重复计算）。</para>
    /// <para><b>精度</b>：<c>MIN(SizeExact)</c> 只有在**所有**行都是精确值时才为 1。
    /// 只要混进一条估算值，合计就只能标成"约"—— 这是刻意的保守做法，
    /// 把估算值当精确值报给用户比不给数字更糟。</para>
    /// </remarks>
    public Task<WriteSummary> SummarizeAsync(LogQuery q) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var whereSql = BuildWhere(q, cmd);
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(Size), 0), COALESCE(MIN(SizeExact), 1)
            FROM Events {whereSql};
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return default;

        return new WriteSummary(
            Count: reader.GetInt64(0),
            TotalBytes: reader.GetInt64(1),
            AllExact: reader.GetInt64(2) != 0);
    });

    /// <summary>
    /// 按**本地日期**分组的写入合计，用于趋势视图。
    /// </summary>
    /// <remarks>
    /// <para>分组用 SQLite 的 <c>'localtime'</c> 修饰符 —— <c>Ts</c> 存的是 Unix 毫秒（UTC），
    /// 而用户心里的"一天"是**本地**的一天。用 UTC 分组会让跨零点的记录落到错误的日期上
    /// （东八区会整体偏移 8 小时，晚上的写入会被算到"明天"）。</para>
    /// </remarks>
    public Task<List<DailyWriteTotal>> DailyTotalsAsync(LogQuery q) => Task.Run(() =>
    {
        var list = new List<DailyWriteTotal>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var whereSql = BuildWhere(q, cmd);
        cmd.CommandText = $"""
            SELECT date(Ts / 1000, 'unixepoch', 'localtime') AS D,
                   COUNT(*), COALESCE(SUM(Size), 0), COALESCE(MIN(SizeExact), 1)
            FROM Events {whereSql}
            GROUP BY D
            ORDER BY D DESC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // date() 返回 'YYYY-MM-DD'
            if (!DateTime.TryParse(reader.GetString(0), out var day)) continue;

            list.Add(new DailyWriteTotal(
                Date: day.Date,
                Count: reader.GetInt64(1),
                TotalBytes: reader.GetInt64(2),
                AllExact: reader.GetInt64(3) != 0));
        }

        return list;
    });

    public Task ClearAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Events; VACUUM;";
        cmd.ExecuteNonQuery();
    });

    /// <summary>导出 CSV（UTF-8 BOM，Excel 可直接打开）。</summary>
    public async Task<int> ExportCsvAsync(string filePath, LogQuery query)
    {
        // 不改动调用方的 query 对象（曾经直接赋值 query.Limit，是个隐式副作用）。
        var rows = await QueryForExportAsync(query).ConfigureAwait(false);

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await sw.WriteLineAsync("时间,文件路径,写入大小(字节),大小是否精确,进程名,PID,触发模式,判定口径,采集模式,是否提醒,聚合条数,备注").ConfigureAwait(false);
        foreach (var r in rows)
        {
            await sw.WriteLineAsync(string.Join(',',
                Csv(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(r.Path),
                r.Size.ToString(),
                r.SizeIsExact ? "精确" : "估算",
                Csv(r.ProcessName),
                r.ProcessId.ToString(),
                Csv(r.TriggerMode),
                Csv(r.ThresholdScope),
                Csv(r.CollectorMode),
                r.Notified ? "是" : "否",
                r.AggregatedCount.ToString(),
                Csv(r.Note))).ConfigureAwait(false);
        }

        return rows.Count;
    }

    /// <summary>导出 JSON。</summary>
    public async Task<int> ExportJsonAsync(string filePath, LogQuery query)
    {
        // 不改动调用方的 query 对象。
        var rows = await QueryForExportAsync(query).ConfigureAwait(false);

        var payload = rows.Select(r => new
        {
            time = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            path = r.Path,
            sizeBytes = r.Size,
            sizeExact = r.SizeIsExact,
            process = r.ProcessName,
            pid = r.ProcessId,
            triggerMode = r.TriggerMode,
            thresholdScope = r.ThresholdScope,
            collectorMode = r.CollectorMode,
            notified = r.Notified,
            aggregatedCount = r.AggregatedCount,
            note = r.Note,
        });

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fs, payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).ConfigureAwait(false);

        return rows.Count;
    }

    private static string Csv(string? s)
    {
        s ??= string.Empty;

        // ★ 公式注入（CSV Injection）防护。
        //   Excel / WPS / LibreOffice 会把以 = + - @ 或制表符开头的单元格当**公式**求值，
        //   而字段内容里「进程名」是攻击者可控的 —— 把可执行文件重命名为
        //   `=cmd|'/c calc'!A1.exe` 再运行它，导出的 CSV 被打开时就会执行。
        //   前置一个单引号即可让表格软件按纯文本处理（单引号本身不显示，不影响可读性）。
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;

        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return '"' + s.Replace("\"", "\"\"") + '"';
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _queue.Writer.TryComplete();
        try { _writer?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Logging;
using CWriteWatcher.Core.Models;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>
/// 日志导出与查询的回归测试。
/// </summary>
/// <remarks>
/// <para>这个类钉死两个真实缺陷：</para>
/// <list type="number">
///   <item><b>CSV 公式注入</b>：字段内容里「进程名」是攻击者可控的 ——
///         把可执行文件重命名为 <c>=cmd|'/c calc'!A1.exe</c> 再运行它，
///         导出的 CSV 在 Excel / WPS 里被打开时就会执行公式。必须在导出时中和。</item>
///   <item><b>导出被静默截断到 5000 条</b>：导出与分页共用了同一个 limit 钳制值，
///         于是"导出全部"实际只导 5000 条，而且提示还写"已导出 5000 条"。</item>
/// </list>
/// </remarks>
public class EventLogStoreExportTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"cwt-logstore-{Guid.NewGuid():N}.db");

    private EventLogStore CreateStartedStore()
    {
        var store = new EventLogStore(new LoggingConfig { AutoTrim = false }, _dbPath);
        store.Start();
        return store;
    }

    private static AlertRecord Record(string path, string process, long size = 1024) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Path = path,
        Size = size,
        SizeIsExact = true,
        ProcessId = 4242,
        ProcessName = process,
        TriggerMode = "B · 超过 10 MB",
        ThresholdScope = "同文件窗口",
        CollectorMode = "精确模式",
        Notified = true,
        AggregatedCount = 1,
    };

    /// <summary>入队并等到全部落盘（写入是后台批量事务）。</summary>
    private static void EnqueueAndWait(EventLogStore store, IEnumerable<AlertRecord> records, int expected)
    {
        foreach (var r in records) store.Enqueue(r);

        var deadline = Environment.TickCount64 + 30_000;
        while (store.WrittenCount < expected && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(expected, store.WrittenCount);
    }

    // ---------------------------------------------------------------- 公式注入

    [Fact]
    public async Task CSV导出必须中和公式注入()
    {
        using var store = CreateStartedStore();

        // 攻击者可控的进程名（把 exe 改成这个名字再运行即可）+ 可疑文件名
        const string maliciousProcess = "=cmd|'/c calc'!A1";
        EnqueueAndWait(store, new[] { Record(@"C:\Users\me\=1+1.txt", maliciousProcess) }, 1);

        var csvPath = Path.Combine(Path.GetTempPath(), $"cwt-export-{Guid.NewGuid():N}.csv");
        try
        {
            var count = await store.ExportCsvAsync(csvPath, new LogQuery { Limit = 500 });
            Assert.Equal(1, count);

            var lines = await File.ReadAllLinesAsync(csvPath);
            Assert.Equal(2, lines.Length);          // 表头 + 1 行

            var data = lines[1];

            // ★ 以 = 开头的字段必须被前置单引号，否则 Excel/WPS 会当公式求值
            Assert.Contains("'=cmd|'/c calc'!A1", data);
            Assert.DoesNotContain(",=cmd", data);

            // 正常字段不受影响
            Assert.Contains(@"C:\Users\me\=1+1.txt", data);
        }
        finally
        {
            try { File.Delete(csvPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task CSV导出必须保留正常数据不做多余改动()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[] { Record(@"C:\Windows\Temp\a.bin", "chrome.exe", 5 * 1024 * 1024) }, 1);

        var csvPath = Path.Combine(Path.GetTempPath(), $"cwt-export-{Guid.NewGuid():N}.csv");
        try
        {
            await store.ExportCsvAsync(csvPath, new LogQuery { Limit = 500 });
            var data = (await File.ReadAllLinesAsync(csvPath))[1];

            Assert.Contains(@"C:\Windows\Temp\a.bin", data);
            Assert.Contains("chrome.exe", data);
            Assert.DoesNotContain("'chrome.exe", data);
        }
        finally
        {
            try { File.Delete(csvPath); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------- 导出截断

    [Fact]
    public async Task 导出不得被截断到5000条()
    {
        const int rows = 5001;   // 刚好越过那个错误的 5000 上限

        using var store = CreateStartedStore();
        EnqueueAndWait(
            store,
            Enumerable.Range(0, rows).Select(i => Record($@"C:\x\f{i}.bin", "chrome.exe")),
            rows);

        var csvPath = Path.Combine(Path.GetTempPath(), $"cwt-export-{Guid.NewGuid():N}.csv");
        try
        {
            var count = await store.ExportCsvAsync(csvPath, new LogQuery { Limit = 300 });

            // ★ 曾经这里是 5000 —— 导出与分页共用钳制值导致静默截断
            Assert.Equal(rows, count);
            Assert.Equal(rows + 1, (await File.ReadAllLinesAsync(csvPath)).Length);
        }
        finally
        {
            try { File.Delete(csvPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task 分页查询仍必须限制在5000条以内()
    {
        using var store = CreateStartedStore();
        EnqueueAndWait(store, new[] { Record(@"C:\x\a.bin", "chrome.exe") }, 1);

        // 分页路径不能因为导出放宽而被撑爆内存
        var page = await store.QueryAsync(new LogQuery { Limit = int.MaxValue });
        Assert.True(page.Count <= 5000);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* ignore */ }
    }
}

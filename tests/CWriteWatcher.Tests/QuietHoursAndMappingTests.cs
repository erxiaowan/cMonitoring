using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;
using CWriteWatcher.Core.Pipeline;
using CWriteWatcher.Core.Util;
using CWriteWatcher.Etw.Parsing;
using Xunit;

namespace CWriteWatcher.Tests;

public class QuietHoursTests
{
    private static QuietHoursConfig Cfg(string start, string end) =>
        new() { Enabled = true, Start = start, End = end };

    [Fact]
    public void 跨零点区间_23点到7点()
    {
        var c = Cfg("23:00", "07:00");

        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(23, 30, 0)));
        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(0, 30, 0)));
        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(6, 59, 0)));
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(7, 0, 0)));
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(12, 0, 0)));
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(22, 59, 0)));
    }

    [Fact]
    public void 不跨零点区间_9点到17点()
    {
        var c = Cfg("09:00", "17:00");

        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(9, 0, 0)));
        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(16, 59, 0)));
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(17, 0, 0)));
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(8, 59, 0)));
    }

    [Fact]
    public void 未启用时永不静默()
    {
        var c = new QuietHoursConfig { Enabled = false, Start = "00:00", End = "23:59" };
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(12, 0, 0)));
    }

    [Fact]
    public void 起止相同时视为全天静默()
    {
        var c = Cfg("08:00", "08:00");
        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(3, 0, 0)));
        Assert.True(QuietHours.IsQuiet(c, new TimeSpan(20, 0, 0)));
    }

    [Fact]
    public void 配置无效时不应静默()
    {
        var c = new QuietHoursConfig { Enabled = true, Start = "abc", End = "xyz" };
        Assert.False(QuietHours.IsQuiet(c, new TimeSpan(12, 0, 0)));
    }
}

public class PathUtilTests
{
    [Theory]
    [InlineData(@"\\?\C:\Windows\Temp", @"C:\Windows\Temp")]
    [InlineData(@"\??\C:\Windows\Temp", @"C:\Windows\Temp")]
    [InlineData(@"C:/Users/alice/Desktop", @"C:\Users\alice\Desktop")]
    [InlineData(@"C:\Windows\\Temp\\", @"C:\Windows\Temp")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    public void 规范化路径(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.Normalize(input));
    }

    [Fact]
    public void UNC前缀应当被正确还原()
    {
        Assert.Equal(@"\\server\share\dir", PathUtil.Normalize(@"\\?\UNC\server\share\dir"));
    }

    [Fact]
    public void 目录包含判定不应误伤同前缀目录()
    {
        Assert.True(PathUtil.IsUnderDirectory(@"C:\Windows\Temp\a.txt", @"C:\Windows\Temp"));
        Assert.True(PathUtil.IsUnderDirectory(@"C:\Windows\Temp", @"C:\Windows\Temp"));
        Assert.False(PathUtil.IsUnderDirectory(@"C:\Windows\Temporary\a.txt", @"C:\Windows\Temp"));
        Assert.False(PathUtil.IsUnderDirectory(@"C:\Win", @"C:\Windows"));
    }

    [Fact]
    public void 稳定哈希应当大小写不敏感且确定()
    {
        var a = PathUtil.StableHash(@"C:\Users\Alice\a.txt");
        var b = PathUtil.StableHash(@"c:\users\alice\A.TXT");
        var c = PathUtil.StableHash(@"C:\Users\Alice\b.txt");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ---------------------------------------------------------------- 交给外壳前的路径校验

    [Theory]
    [InlineData(@"C:\Users\me\a.txt")]
    [InlineData(@"C:\Windows\Temp\a b.bin")]
    [InlineData(@"C:\Users\me\带空格 和中文.txt")]
    public void 普通本地路径应当允许交给外壳打开(string path)
    {
        Assert.True(PathUtil.IsShellSafePath(path), $"{path} 应当被判定为安全");
    }

    [Theory]
    [InlineData(@"C:\evil"" /root,C:\", "含引号会破坏 explorer 的引号配对")]
    [InlineData("C:\\a\r\nb.txt", "含换行可注入额外参数")]
    [InlineData("C:\\a\0b.txt", "含 NUL")]
    [InlineData(@"\\attacker\share\a.txt", "UNC 路径会触发一次 SMB 连接")]
    [InlineData("", "空路径")]
    [InlineData("   ", "全空白")]
    [InlineData(null, "null")]
    public void 危险路径必须被拒绝(string? path, string reason)
    {
        Assert.False(PathUtil.IsShellSafePath(path), $"应当拒绝（{reason}）：{path}");
    }

    [Fact]
    public void 设备路径应当转换为盘符路径()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\Device\HarddiskVolume3"] = "C:",
        };

        var result = PathUtil.ConvertDevicePathToDriveLetter(
            @"\Device\HarddiskVolume3\Users\alice\a.txt", map);

        Assert.Equal(@"C:\Users\alice\a.txt", result);
    }

    [Fact]
    public void 未知设备路径应当原样规范化返回()
    {
        var result = PathUtil.ConvertDevicePathToDriveLetter(
            @"\Device\HarddiskVolume9\foo\bar.txt", new Dictionary<string, string>());

        Assert.Equal(@"\Device\HarddiskVolume9\foo\bar.txt", result);
    }

    [Fact]
    public void 只关心C盘路径()
    {
        Assert.True(PathUtil.IsOnDriveC(@"C:\a"));
        Assert.True(PathUtil.IsOnDriveC(@"c:\a"));
        Assert.False(PathUtil.IsOnDriveC(@"D:\a"));
        Assert.False(PathUtil.IsOnDriveC(null));
        Assert.False(PathUtil.IsOnDriveC(""));
    }

    [Fact]
    public void 取父目录()
    {
        Assert.Equal(@"C:\a\b", PathUtil.GetParent(@"C:\a\b\c"));
        Assert.Equal(@"C:\", PathUtil.GetParent(@"C:\a"));
        Assert.Null(PathUtil.GetParent(@"C:\"));
    }
}

public class FileKeyPathMapTests
{
    private static FileKeyPathMap NewMap() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\Device\HarddiskVolume3"] = "C:",
        });

    [Fact]
    public void Create建立映射_Close移除映射()
    {
        var m = NewMap();

        m.Set(1, @"\Device\HarddiskVolume3\Users\a\file.txt", 1000);

        Assert.True(m.TryGet(1, 1000, out var path));
        Assert.Equal(@"C:\Users\a\file.txt", path);
        Assert.Equal(1, m.Hits);

        m.Remove(1);

        Assert.False(m.TryGet(1, 1000, out _));
        Assert.Equal(1, m.Misses);
    }

    [Fact]
    public void 映射缺失时计入未命中()
    {
        var m = NewMap();

        Assert.False(m.TryGet(999, 1000, out var path));
        Assert.Null(path);
        Assert.Equal(1, m.Misses);
        Assert.Equal(0, m.HitRate);
    }

    [Fact]
    public void 排除路径应当释放字符串但保留标记()
    {
        var m = NewMap();
        m.Set(7, @"\Device\HarddiskVolume3\Windows\Temp\a.tmp", 1000);

        m.MarkExcluded(7, 1000);

        // 仍算"命中"（知道它是被排除的），但 Path 为空
        Assert.True(m.TryGet(7, 1000, out var path));
        Assert.Equal(string.Empty, path);
        Assert.Equal(1, m.ExcludedReleases);
    }

    [Fact]
    public void Rename应当更新路径()
    {
        var m = NewMap();
        m.Set(5, @"\Device\HarddiskVolume3\a\old.txt", 1000);
        m.Set(5, @"\Device\HarddiskVolume3\b\new.txt", 1010);

        Assert.True(m.TryGet(5, 1010, out var path));
        Assert.Equal(@"C:\b\new.txt", path);
    }

    [Fact]
    public void FileKey为零时应当被忽略()
    {
        var m = NewMap();
        m.Set(0, @"\Device\HarddiskVolume3\a.txt", 1000);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void 路径为空时应当被忽略()
    {
        var m = NewMap();
        m.Set(1, null, 1000);
        m.Set(2, string.Empty, 1000);
        Assert.Equal(0, m.Count);
    }
}

public class AlertAggregatorTests
{
    private static AlertRecord Rec(int pid, string path, long size) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Path = path,
        Size = size,
        SizeIsExact = true,
        ProcessId = pid,
        ProcessName = "test.exe",
        TriggerMode = "B",
        ThresholdScope = "P2",
        CollectorMode = "精确",
        Notified = true,
    };

    [Fact]
    public void 同一进程的多条提醒应当合并为一条()
    {
        var a = new AlertAggregator(windowMs: 5000, maxItems: 200);

        a.Add(Rec(100, @"C:\a.bin", 11 * 1024 * 1024), 1000);
        a.Add(Rec(100, @"C:\b.bin", 12 * 1024 * 1024), 1100);
        a.Add(Rec(100, @"C:\c.bin", 15 * 1024 * 1024), 1200);

        var flushed = a.DrainExpired(nowMs: 7000);

        Assert.Single(flushed);
        Assert.Equal(3, flushed[0].AggregatedCount);
        Assert.Equal((11 + 12 + 15) * 1024 * 1024, flushed[0].Size);
        Assert.Contains("等 3 个文件", flushed[0].Path);
        Assert.Contains(@"C:\c.bin", flushed[0].Path); // 最大的是 c
    }

    [Fact]
    public void 窗口未到期时不应冲刷()
    {
        var a = new AlertAggregator(windowMs: 5000, maxItems: 200);
        a.Add(Rec(100, @"C:\a.bin", 1024), 1000);

        Assert.Empty(a.DrainExpired(nowMs: 2000));
        Assert.Single(a.DrainExpired(nowMs: 7000));
    }

    [Fact]
    public void 单条时不应标记为聚合()
    {
        var a = new AlertAggregator(windowMs: 1000, maxItems: 200);
        a.Add(Rec(100, @"C:\a.bin", 1024), 1000);

        var flushed = a.DrainExpired(3000);

        Assert.Single(flushed);
        Assert.Equal(1, flushed[0].AggregatedCount);
        Assert.Equal(@"C:\a.bin", flushed[0].Path);
    }

    [Fact]
    public void 不同进程应当各自成桶()
    {
        var a = new AlertAggregator(windowMs: 1000, maxItems: 200);
        a.Add(Rec(1, @"C:\a.bin", 1024), 1000);
        a.Add(Rec(2, @"C:\b.bin", 2048), 1000);

        var flushed = a.DrainExpired(3000);

        Assert.Equal(2, flushed.Count);
    }

    [Fact]
    public void 达到条数上限时应当立即冲刷()
    {
        var a = new AlertAggregator(windowMs: 60_000, maxItems: 3);
        var captured = new List<AlertRecord>();
        a.PendingFlush += list => captured.AddRange(list);

        for (var i = 0; i < 3; i++) a.Add(Rec(1, $@"C:\f{i}.bin", 1024), 1000 + i);

        Assert.NotEmpty(captured);
    }

    // 回归：Size 全为 0 时路径丢失（模式 A 下新建空文件会产生 size=0 的 ADDED 事件）
    [Fact]
    public void 单条零大小事件_提醒仍应带上文件路径()
    {
        var a = new AlertAggregator(windowMs: 1000, maxItems: 200);
        a.Add(Rec(100, @"C:\Users\a\empty.txt", 0), 1000);

        var flushed = a.DrainExpired(3000);

        Assert.Single(flushed);
        Assert.Equal(@"C:\Users\a\empty.txt", flushed[0].Path);
        Assert.Equal(0, flushed[0].Size);
    }

    [Fact]
    public void 多条零大小事件聚合_路径不应退化为共N个文件()
    {
        var a = new AlertAggregator(windowMs: 1000, maxItems: 200);
        a.Add(Rec(100, @"C:\Users\a\one.txt", 0), 1000);
        a.Add(Rec(100, @"C:\Users\a\two.txt", 0), 1100);
        a.Add(Rec(100, @"C:\Users\a\three.txt", 0), 1200);

        var flushed = a.DrainExpired(3000);

        Assert.Single(flushed);
        Assert.Contains(@"C:\Users\a\one.txt", flushed[0].Path);
        Assert.DoesNotContain("共 3 个文件", flushed[0].Path);
        Assert.Contains("等 3 个文件", flushed[0].Path);
    }

    [Fact]
    public void 零大小事件与有大小事件混合_应取最大的那个路径()
    {
        var a = new AlertAggregator(windowMs: 1000, maxItems: 200);
        a.Add(Rec(100, @"C:\Users\a\empty.txt", 0), 1000);
        a.Add(Rec(100, @"C:\Users\a\big.bin", 11 * 1024 * 1024), 1100);

        var flushed = a.DrainExpired(3000);

        Assert.Single(flushed);
        Assert.Contains(@"C:\Users\a\big.bin", flushed[0].Path);
    }
}

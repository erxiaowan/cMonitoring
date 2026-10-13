using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Filtering;
using Xunit;

namespace CWriteWatcher.Tests;

public class ExclusionMatcherTests
{
    private static ExclusionMatcher BuildDefault() =>
        new(DefaultExclusions.BuildPathRules(), DefaultExclusions.BuildProcessNames());

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\Windows\Temp\foo.tmp")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\abc.cab")]
    [InlineData(@"C:\Windows\WinSxS\amd64_foo\bar.dll")]
    [InlineData(@"C:\Windows\System32\winevt\Logs\System.evtx")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21\deleted.txt")]
    [InlineData(@"C:\System Volume Information\foo")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Scans\a.bin")]
    [InlineData(@"C:\ProgramData\Microsoft\Search\Data\Windows.edb")]
    [InlineData(@"C:\Windows\Prefetch\FOO.pf")]
    public void 内置规则应当排除高频系统路径(string path)
    {
        var m = BuildDefault();
        Assert.True(m.IsExcludedPath(path), $"应当排除：{path}");
    }

    [Theory]
    [InlineData(@"C:\Users\alice\Documents\report.docx")]
    [InlineData(@"C:\Users\alice\Desktop\a.txt")]
    [InlineData(@"C:\Projects\myapp\src\Program.cs")]
    [InlineData(@"C:\ProgramData\MyVendor\config.json")]
    public void 不应误伤普通用户文件(string path)
    {
        var m = BuildDefault();
        Assert.False(m.IsExcludedPath(path), $"不应排除：{path}");
    }

    [Theory]
    [InlineData(@"C:\Users\alice\Downloads\setup.exe.crdownload")]
    [InlineData(@"C:\Users\alice\AppData\Local\Temp\x.tmp")]
    [InlineData(@"C:\somewhere\a.etl")]
    public void 应当按扩展名排除临时文件(string path)
    {
        var m = BuildDefault();
        Assert.True(m.IsExcludedPath(path), $"应当排除：{path}");
    }

    [Fact]
    public void 目录前缀匹配应当大小写不敏感()
    {
        var m = BuildDefault();
        Assert.True(m.IsExcludedPath(@"c:\windows\temp\A.TXT"));
        Assert.True(m.IsExcludedPath(@"C:\WINDOWS\TEMP\A.TXT"));
    }

    [Fact]
    public void 目录规则不应匹配同前缀的兄弟目录()
    {
        // C:\Windows\Temp 的规则不应误伤 C:\Windows\Temporary
        var rules = new List<ExclusionRule>
        {
            new() { Pattern = @"C:\Windows\Temp\", Enabled = true },
        };
        var m = new ExclusionMatcher(rules, null);

        Assert.True(m.IsExcludedPath(@"C:\Windows\Temp\a.txt"));
        Assert.True(m.IsExcludedPath(@"C:\Windows\Temp"));
        Assert.False(m.IsExcludedPath(@"C:\Windows\Temporary\a.txt"));
    }

    [Fact]
    public void 通配规则应当正确匹配()
    {
        var rules = new List<ExclusionRule>
        {
            new() { Pattern = @"C:\MyApp\Logs\*.log", Enabled = true },
            new() { Pattern = "*.bak", Enabled = true },
        };
        var m = new ExclusionMatcher(rules, null);

        Assert.True(m.IsExcludedPath(@"C:\MyApp\Logs\2026-01-01.log"));
        Assert.False(m.IsExcludedPath(@"C:\MyApp\Logs\2026-01-01.txt"));
        Assert.True(m.IsExcludedPath(@"C:\anywhere\deep\file.bak"));
        Assert.False(m.IsExcludedPath(@"C:\anywhere\deep\file.bak2"));
    }

    [Fact]
    public void 禁用的规则不应生效()
    {
        var rules = new List<ExclusionRule>
        {
            new() { Pattern = @"C:\Foo\", Enabled = false },
            new() { Pattern = @"C:\Bar\", Enabled = true },
        };
        var m = new ExclusionMatcher(rules, null);

        Assert.False(m.IsExcludedPath(@"C:\Foo\a.txt"));
        Assert.True(m.IsExcludedPath(@"C:\Bar\a.txt"));
    }

    [Fact]
    public void 进程黑名单应当大小写不敏感()
    {
        var m = new ExclusionMatcher(Array.Empty<ExclusionRule>(), new[] { "MsMpEng.exe", "SearchIndexer.exe" });

        Assert.True(m.IsExcludedProcess("msmpeng.exe"));
        Assert.True(m.IsExcludedProcess("MSMPENG.EXE"));
        Assert.False(m.IsExcludedProcess("chrome.exe"));
        Assert.False(m.IsExcludedProcess(null));
    }

    [Fact]
    public void 环境变量应当被展开()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rules = new List<ExclusionRule>
        {
            new() { Pattern = @"%LOCALAPPDATA%\MyCache\", Enabled = true },
        };
        var m = new ExclusionMatcher(rules, null);

        Assert.True(m.IsExcludedPath(Path.Combine(localAppData, "MyCache", "a.dat")));
    }

    // ---------------------------------------------------------------- 通配符 → 正则的灾难性回溯

    /// <summary>从匹配器里取出第 n 条全路径通配规则编译出的正则文本（用于结构性断言）。</summary>
    private static string? CompiledPattern(ExclusionMatcher matcher, int index = 0)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        foreach (var field in new[] { "_fullPathWildcards", "_fileNameWildcards" })
        {
            if (matcher.GetType().GetField(field, flags)?.GetValue(matcher) is not Array arr) continue;
            if (arr.Length <= index) continue;

            var item = arr.GetValue(index)!;
            var inner = item.GetType().GetField("_regex", flags)?.GetValue(item);
            return inner?.ToString();
        }

        return null;
    }

    [Fact]
    public void 排除规则里的连续星号必须被折叠()
    {
        var matcher = new ExclusionMatcher(
            new[] { new ExclusionRule { Pattern = @"C://x//**********.tmp" } }, null);

        var pattern = CompiledPattern(matcher);
        Assert.NotNull(pattern);

        // ★ `**` 与 `*` 语义等价，但 `.*.*` 会让匹配失败时的回溯代价指数上升。
        Assert.DoesNotContain(".*.*", pattern!);
        Assert.Contains(".*", pattern!);          // 星号仍然生效
    }

    [Fact]
    public void 连续星号的匹配结果必须与单个星号完全一致()
    {
        var single = new ExclusionMatcher(new[] { new ExclusionRule { Pattern = @"C://x//*.tmp" } }, null);
        var many = new ExclusionMatcher(new[] { new ExclusionRule { Pattern = @"C://x//**********.tmp" } }, null);

        foreach (var path in new[]
                 {
                     @"C://x//a.tmp", @"C://x//a.b.tmp", @"C://x//a.txt", @"C://y//a.tmp", @"C://x//tmp",
                 })
        {
            Assert.Equal(single.IsExcludedPath(path), many.IsExcludedPath(path));
        }
    }

    [Fact]
    public void 交替星号的病态规则必须被超时熔断而不是挂住()
    {
        // 交替星号折叠不掉：`*a*a*a*a*a*a*a*ab` → `^.*a.*a.*a.*a.*a.*a.*a.*ab$`。
        // 用一个「结尾对不上」的输入触发失败匹配，修复前会指数级回溯。
        var matcher = new ExclusionMatcher(
            new[] { new ExclusionRule { Pattern = "*a*a*a*a*a*a*a*ab" } }, null);

        var fileName = new string('a', 40);
        var path = @"C://" + fileName;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = matcher.IsExcludedPath(path);
        sw.Stop();

        Assert.False(result);

        // 匹配超时设的是 50ms，正常应当在百毫秒内返回。
        //
        // ★ 阈值必须卡紧：实测这条扁平正则（8 组 .*a）在 40 字符输入上
        //   无超时时要跑 1641 ms —— 而真实文件路径可达 260 字符，
        //   再叠加更多星号会直接挂死进程。
        //   曾经把阈值写成 2000 ms，结果"去掉超时也能过"，测试形同虚设。
        Assert.True(sw.ElapsedMilliseconds < 300,
            $"病态规则让匹配耗时 {sw.ElapsedMilliseconds} ms —— 匹配超时没生效？" +
            "（无超时时实测约 1641 ms）");
    }


    // ---------------------------------------------------------------- 排除表规模上限

    [Fact]
    public void 配置里的排除规则条数与长度必须被钳制()
    {
        var cfg = new CWriteWatcher.Core.Config.AppConfig();
        cfg.Exclusions!.Rules!.Clear();

        // 6000 条正常规则 + 1 条超长模式
        for (var i = 0; i < 6000; i++)
            cfg.Exclusions.Rules.Add(new ExclusionRule { Pattern = $@"C:////x////f{i}.tmp" });
        cfg.Exclusions.Rules.Add(new ExclusionRule { Pattern = new string('a', 5000) });

        var store = new CWriteWatcher.Core.Config.ConfigStore(
            Path.Combine(Path.GetTempPath(), $"cwt-cfgcap-{Guid.NewGuid():N}.json"));
        var saved = store.Update(c =>
        {
            c.Exclusions!.Rules!.Clear();
            c.Exclusions.Rules.AddRange(cfg.Exclusions.Rules);
        });

        // 条数被钳到上限
        Assert.True(saved.Exclusions!.Rules!.Count <= 5000,
            $"规则条数没有被钳制，实际 {saved.Exclusions.Rules.Count}");

        // 超长模式被丢弃（不是截断 —— 截断会扩大排除范围）
        Assert.DoesNotContain(saved.Exclusions.Rules, r => r.Pattern.Length > 500);
    }

}

using System.Text;
using System.Text.RegularExpressions;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Util;

namespace CWriteWatcher.Core.Filtering;

/// <summary>
/// 排除表匹配器。设计目标：在每秒数万事件的场景下保持零分配（快路径）。
/// </summary>
/// <remarks>
/// 规则推断（用户在 UI 里输入的形式）：
/// <list type="bullet">
///   <item>以 <c>\</c> 或 <c>\*</c> 结尾 → 目录前缀匹配（该目录及其所有子孙）。</item>
///   <item>含 <c>*</c> 或 <c>?</c> 且不含 <c>\</c> → 只匹配文件名（如 <c>*.tmp</c>）。</item>
///   <item>含 <c>*</c> 或 <c>?</c> 且含 <c>\</c> → 对完整路径做通配匹配。</item>
///   <item>其余 → 精确路径匹配。</item>
/// </list>
/// </remarks>
public sealed class ExclusionMatcher
{
    private readonly string[] _dirPrefixes;
    private readonly HashSet<string> _exactPaths;
    private readonly WildcardRegex[] _fullPathWildcards;
    private readonly WildcardRegex[] _fileNameWildcards;
    private readonly string[] _extensionSuffixes;
    private readonly HashSet<string> _processNames;

    public int RuleCount => _dirPrefixes.Length + _exactPaths.Count + _fullPathWildcards.Length
                            + _fileNameWildcards.Length + _extensionSuffixes.Length;

    public IReadOnlyCollection<string> ProcessNames => _processNames;

    public ExclusionMatcher(IEnumerable<ExclusionRule> rules, IEnumerable<string>? processNames)
    {
        var dirs = new List<string>();
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullWild = new List<WildcardRegex>();
        var nameWild = new List<WildcardRegex>();
        var exts = new List<string>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;

            var raw = rule.Pattern;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var p = PathUtil.NormalizeRule(raw);
            if (p.Length == 0) continue;

            var hasWildcard = p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0;

            if (!hasWildcard)
            {
                // 目录前缀 or 精确路径
                var isDir = raw.TrimEnd().EndsWith('\\') || Directory.Exists(p);
                if (isDir) dirs.Add(p.TrimEnd('\\'));
                else exact.Add(p);
                continue;
            }

            // 纯扩展名形式 *.ext → 后缀匹配（最快）
            if (p.Length > 1 && p[0] == '*' && p[1] == '.' && p.IndexOf('*', 1) < 0 && p.IndexOf('?') < 0)
            {
                exts.Add(p.Substring(1)); // ".tmp"
                continue;
            }

            var regex = BuildRegex(p);
            if (p.IndexOf('\\') >= 0) fullWild.Add(regex);
            else nameWild.Add(regex);
        }

        // 目录前缀按长度降序，便于尽早命中
        dirs.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        _dirPrefixes = dirs.ToArray();
        _exactPaths = exact;
        _fullPathWildcards = fullWild.ToArray();
        _fileNameWildcards = nameWild.ToArray();
        _extensionSuffixes = exts.ToArray();

        _processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processNames is not null)
        {
            foreach (var n in processNames)
            {
                if (!string.IsNullOrWhiteSpace(n)) _processNames.Add(n.Trim());
            }
        }
    }

    private static WildcardRegex BuildRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2 + 8);
        sb.Append('^');
        var lastWasStar = false;

        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*':
                    // ★ 折叠连续的 `*`：`**` 与 `*` 语义完全等价，
                    //   但 `.*.*` 会让匹配失败时的回溯代价指数上升（灾难性回溯）。
                    if (lastWasStar) continue;
                    sb.Append(".*");
                    lastWasStar = true;
                    break;

                case '?':
                    sb.Append('.');
                    lastWasStar = false;
                    break;

                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    lastWasStar = false;
                    break;
            }
        }

        sb.Append('$');
        return new WildcardRegex(sb.ToString());
    }

    /// <summary>路径是否被排除。传入的路径应已由 <see cref="PathUtil.Normalize"/> 规范化。</summary>
    public bool IsExcludedPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty) return false;

        // 1) 目录前缀（最快，且覆盖绝大多数规则）
        foreach (var dir in _dirPrefixes)
        {
            if (path.Length < dir.Length) continue;
            if (PathUtil.IsUnderDirectory(path, dir)) return true;
        }

        // 2) 精确路径
        if (_exactPaths.Count > 0 && _exactPaths.Contains(path.ToString())) return true;

        // 3) 扩展名后缀
        if (_extensionSuffixes.Length > 0)
        {
            foreach (var ext in _extensionSuffixes)
            {
                if (path.Length > ext.Length && path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        // 4) 文件名通配 / 全路径通配（较慢，放最后）
        if (_fileNameWildcards.Length > 0 || _fullPathWildcards.Length > 0)
        {
            var s = path.ToString();
            if (_fileNameWildcards.Length > 0)
            {
                var slash = s.LastIndexOf('\\');
                var name = slash >= 0 ? s.Substring(slash + 1) : s;
                foreach (var rx in _fileNameWildcards)
                {
                    if (rx.IsMatch(name)) return true;
                }
            }
            foreach (var rx in _fullPathWildcards)
            {
                if (rx.IsMatch(s)) return true;
            }
        }

        return false;
    }


/// <summary>
/// 通配符规则编译出来的正则，带**回溯熔断**。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这一层：</b>排除规则是用户可编辑的通配符，编译时逐字符转成正则
/// （<c>*</c> → <c>.*</c>，其余字符 <c>Regex.Escape</c>）。于是 <c>**********</c>
/// 会变成 <c>^.*.*.*.*.*.*.*.*.*.*$</c> —— 这是典型的<b>灾难性回溯</b>构造：
/// 匹配失败时引擎要枚举所有切分方式，代价随 <c>.*</c> 个数指数上升。</para>
/// <para>而这条匹配跑在 <b>ETW 热点路径</b>上（每个写入事件都要过一遍），
/// 进程还可能是管理员权限 —— 一个手滑写错或被人改过的配置就能把 CPU 挂住。</para>
/// <para><b>三重防护：</b>
/// ① 编译时折叠连续的 <c>*</c>（见 <c>BuildRegex</c>）；
/// ② 给正则设匹配超时；
/// ③ 一旦超时就<b>永久停用该条规则</b> —— 否则每个事件都要白等一次超时，仍然是 DoS。</para>
/// </remarks>
internal sealed class WildcardRegex
{
    /// <summary>单条规则的匹配上限。正常的路径匹配在微秒级，50ms 已是极宽松的上限。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Regex _regex;
    private int _disabled;   // 0 = 正常，1 = 已熔断

    public WildcardRegex(string pattern)
    {
        _regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
    }

    /// <summary>该规则是否因回溯超时被熔断。</summary>
    public bool Disabled => Volatile.Read(ref _disabled) != 0;

    public bool IsMatch(string input)
    {
        if (Disabled) return false;

        try
        {
            return _regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Volatile.Write(ref _disabled, 1);
            return false;
        }
    }

    public bool IsMatch(ReadOnlySpan<char> input)
    {
        if (Disabled) return false;

        try
        {
            return _regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Volatile.Write(ref _disabled, 1);
            return false;
        }
    }
}

    /// <summary>进程名是否在黑名单中。</summary>
    public bool IsExcludedProcess(string? processName) =>
        !string.IsNullOrEmpty(processName) && _processNames.Contains(processName);

    /// <summary>
    /// 从配置构建匹配器。内置默认规则在 <see cref="Config.ConfigStore"/> 首次加载时就已经
    /// 播种进 <c>config.Exclusions.Rules</c>，因此这里只读配置本身——
    /// 这样用户在设置界面里对内置规则的启用/禁用才能被正确持久化。
    /// </summary>
    public static ExclusionMatcher Build(AppConfig config, int selfProcessId)
    {
        _ = selfProcessId; // 自身 PID 的排除由管道第一行负责，这里不需要
        return new ExclusionMatcher(config.Exclusions.Rules, config.Exclusions.ExcludedProcesses);
    }
}

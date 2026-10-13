using System.Runtime.InteropServices;

namespace CWriteWatcher.Core.Util;

/// <summary>路径规范化与环境变量展开。所有比较使用 OrdinalIgnoreCase，不额外保存大写副本。</summary>
public static class PathUtil
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CWriteWatcher");

    public static string LocalAppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CWriteWatcher");

    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");
    public static string DatabasePath => Path.Combine(AppDataDir, "events.db");

    /// <summary>展开 %VAR% 环境变量。</summary>
    public static string ExpandEnv(string? raw) =>
        string.IsNullOrEmpty(raw) ? string.Empty : Environment.ExpandEnvironmentVariables(raw);

    /// <summary>
    /// 规范化路径：展开环境变量 → 剥离 \\?\ 与 \??\ 前缀 → 统一分隔符 → 折叠重复分隔符 → 去掉尾部反斜杠。
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var p = path.Trim();

        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            p = @"\\" + p.Substring(8);
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p.Substring(4);
        }

        if (p.IndexOf('/') >= 0) p = p.Replace('/', '\\');

        // 折叠重复分隔符（保留 UNC 前导的 \\）
        if (p.IndexOf(@"\\", 1, StringComparison.Ordinal) >= 0)
        {
            var sb = new System.Text.StringBuilder(p.Length);
            var start = p.StartsWith(@"\\", StringComparison.Ordinal) ? 2 : 0;
            if (start == 2) sb.Append(@"\\");
            for (var i = start; i < p.Length; i++)
            {
                if (p[i] == '\\' && sb.Length > 0 && sb[^1] == '\\') continue;
                sb.Append(p[i]);
            }
            p = sb.ToString();
        }

        // 去掉尾部反斜杠（但保留 "C:\" 这种根形式）
        while (p.Length > 3 && p[^1] == '\\') p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";

        return p;
    }

    /// <summary>规范化并展开环境变量，用于排除规则。</summary>
    public static string NormalizeRule(string? rule) => Normalize(ExpandEnv(rule));

    /// <summary>判断 <paramref name="path"/> 是否位于目录 <paramref name="dir"/> 之下（含自身）。</summary>
    public static bool IsUnderDirectory(ReadOnlySpan<char> path, ReadOnlySpan<char> dir)
    {
        if (dir.IsEmpty) return false;
        if (path.Length < dir.Length) return false;
        if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == dir.Length) return true;

        var d = dir[^1];
        var p = path[dir.Length];
        if (d == '\\') return true;
        return p == '\\';
    }

    /// <summary>取父目录；已是根（如 <c>C:\</c>）则返回 null。</summary>
    public static string? GetParent(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var trimmed = path.TrimEnd('\\');

        // "C:" 本身就是根
        if (trimmed.Length == 2 && trimmed[1] == ':') return null;
        if (trimmed.Length == 0) return null;

        var idx = trimmed.LastIndexOf('\\');
        if (idx < 0) return null;

        // "C:\a" → "C:\"
        if (idx == 2 && trimmed.Length > 2 && trimmed[1] == ':') return trimmed[..3];

        return trimmed[..idx];
    }

    /// <summary>是否为 C 盘路径。</summary>
    public static bool IsOnDriveC(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && char.ToUpperInvariant(path[0]) == 'C' && path[1] == ':';

    /// <summary>稳定哈希（FNV-1a 64 位，大小写不敏感），用于把路径映射为阈值窗口键。</summary>
    public static long StableHash(ReadOnlySpan<char> s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var ch in s)
        {
            var c = (ulong)char.ToUpperInvariant(ch);
            hash ^= c & 0xFF;
            hash *= prime;
            hash ^= (c >> 8) & 0xFF;
            hash *= prime;
        }
        return unchecked((long)hash);
    }

    /// <summary>
    /// NT 设备路径（<c>\Device\HarddiskVolumeN\...</c>）→ 盘符路径。
    /// </summary>
    /// <remarks>
    /// 注意设备名是**前两段**（<c>\Device\HarddiskVolume3</c>），不是第一段——
    /// 只取第一段会得到 <c>\Device</c> 从而匹配失败。
    /// </remarks>
    public static string ConvertDevicePathToDriveLetter(string ntPath, IReadOnlyDictionary<string, string> deviceMap)
    {
        if (string.IsNullOrEmpty(ntPath)) return string.Empty;
        if (ntPath[0] != '\\') return Normalize(ntPath);

        var firstSep = ntPath.IndexOf('\\', 1);
        if (firstSep < 0) return Normalize(ntPath);

        var secondSep = ntPath.IndexOf('\\', firstSep + 1);
        var device = secondSep < 0 ? ntPath : ntPath[..secondSep];
        var rest = secondSep < 0 ? string.Empty : ntPath[secondSep..];

        if (deviceMap.TryGetValue(device, out var drive))
        {
            return Normalize(drive + rest);
        }

        return Normalize(ntPath);
    }

    /// <summary>
    /// 判断路径是否可以安全地交给外壳（<c>explorer.exe / Process.Start</c>）打开。
    /// </summary>
    /// <remarks>
    /// <para>日志里的路径来自文件系统，而 Windows 文件名本身就禁止 <c>"</c>、CR、LF、NUL，
    /// 所以正常情况下永远返回 <c>true</c>。这一层是<b>防御性</b>校验：
    /// 路径最终会被拼进 <c>explorer.exe /select,"&lt;path&gt;"</c> 的命令行，
    /// 一旦含引号就能破坏引号配对、往 explorer 的参数里注入内容。</para>
    /// <para>另外也拒绝 UNC 路径：对 <c>\\server\share</c> 做 <c>File.Exists</c> / <c>Directory.Exists</c>
    /// 会发起一次 SMB 连接（可用于探测内网或触发 NTLM 认证）。
    /// 本工具只监控本地盘，出现 UNC 路径说明数据被篡改过，直接拒绝更安全。</para>
    /// </remarks>
    public static bool IsShellSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // 命令行的引号与换行、以及字符串终止符
        if (path.IndexOfAny(UnsafeInShell) >= 0) return false;

        // UNC（\\server\share）：本地盘符路径不会长这样
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        return true;
    }

    private static readonly char[] UnsafeInShell = { '"', '\r', '\n', '\0' };

    public static void EnsureDirectory(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    /// <summary>枚举 A: ~ Z: 的 NT 设备名映射（用于把 ETW 的 NT 路径转成盘符路径）。</summary>
    public static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];

        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\'); // "C:"
            try
            {
                var len = QueryDosDevice(letter, buffer, (uint)buffer.Length);
                if (len == 0) continue;
                var target = new string(buffer, 0, (int)len).TrimEnd('\0');
                var first = target.Split('\0')[0];
                if (!string.IsNullOrEmpty(first)) map[first] = letter;
            }
            catch
            {
                // 忽略无权限的卷
            }
        }

        return map;
    }
}

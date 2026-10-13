using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;

namespace CWriteWatcher.Etw.Parsing;

/// <summary>PID → 进程名 缓存。进程退出后 PID 会被复用，因此带 TTL。</summary>
internal sealed class ProcessNameCache
{
    private readonly struct Entry
    {
        public readonly string Name;
        public readonly long Tick;

        public Entry(string name, long tick)
        {
            Name = name;
            Tick = tick;
        }
    }

    private readonly ConcurrentDictionary<int, Entry> _cache = new();
    private readonly int _ttlMs = 30_000;

    public string? GetName(int pid)
    {
        if (pid <= 0) return null;

        var now = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var e) && now - e.Tick < _ttlMs) return e.Name;

        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            name = string.Empty; // 进程已退出或无权访问
        }

        _cache[pid] = new Entry(name, now);
        return name.Length == 0 ? null : name;
    }

    /// <summary>当前进程是否以管理员身份运行（精确模式的硬性前提）。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

using System.Text;
using CWriteWatcher.Core.Util;

namespace CWriteWatcher.App.Services;

/// <summary>
/// 极轻量的运行日志：记录启动、退出、提权、采集器状态变化与未处理异常。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它：</b>托盘程序崩溃或异常退出时，用户只会看到"图标消失了"，
/// 没有任何线索——事件日志里也常常什么都没有。排查这类问题需要程序自己留下痕迹。</para>
/// <para>写入 <c>%AppData%\CWriteWatcher\run.log</c>，单文件、追加、出错时静默忽略
/// （日志本身绝不能影响主流程）。</para>
/// </remarks>
internal static class RunLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(PathUtil.AppDataDir, "run.log");

    public static string FilePath => LogPath;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                PathUtil.EnsureDirectory(PathUtil.AppDataDir);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响程序本身
        }
    }

    /// <summary>记录未处理异常（含类型、消息与完整堆栈）。</summary>
    public static void WriteException(string source, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(source).Append("] 未处理异常");
        sb.Append(Environment.NewLine).Append(ex?.ToString() ?? "(无异常对象)");
        Write(sb.ToString());
    }

    /// <summary>裁剪过大的日志文件，避免无限增长。</summary>
    public static void TrimIfTooLarge(long maxBytes = 2 * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length <= maxBytes) return;

            lock (Gate)
            {
                // 只保留后一半
                var text = File.ReadAllText(LogPath, Encoding.UTF8);
                var keep = text.Length / 2;
                File.WriteAllText(LogPath, "…（前部已裁剪）…" + Environment.NewLine + text[^keep..], Encoding.UTF8);
            }
        }
        catch
        {
            // ignore
        }
    }
}

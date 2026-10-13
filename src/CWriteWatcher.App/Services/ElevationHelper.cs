using System.Diagnostics;

namespace CWriteWatcher.App.Services;

/// <summary>管理员权限相关的小工具。</summary>
internal static class ElevationHelper
{
    /// <summary>当前进程是否以管理员身份运行（精确模式的前提）。</summary>
    public static bool IsElevated => CWriteWatcher.Etw.EtwKernelFileCollector.IsAvailable;

    /// <summary>
    /// 以管理员身份重新启动本程序（会触发一次 UAC 确认）。
    /// </summary>
    /// <returns>成功发起重启返回 true；用户在 UAC 处取消或启动失败返回 false。</returns>
    /// <remarks>
    /// <para><b>本方法不负责退出当前实例</b>——退出方式两个调用点不同：</para>
    /// <list type="bullet">
    ///   <item>设置窗体里：消息循环已运行，调用 <c>Application.Exit()</c>。</item>
    ///   <item>Program.Main 启动路径上：<c>Application.Run()</c> 还没开始，此时
    ///         <c>Application.Exit()</c> 是无效的（没有消息循环），直接 <c>return</c> 让进程自然结束。</item>
    /// </list>
    /// <para>两个调用点：设置里切换到精确模式时；以及程序启动时发现配置是精确模式但当前不是管理员。
    /// 后者不能省——用户下次双击启动或开机自启时配置已经是精确模式，
    /// 若不做检查，程序会以普通权限启动、ETW 必然失败，用户只会看到一句「拒绝访问」而不知如何是好。</para>
    /// </remarks>
    public static bool RelaunchElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--tray",
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 对话框点「否」时抛 Win32Exception(1223 = ERROR_CANCELLED)
            MessageBox.Show(
                "以管理员身份重启失败：" + ex.Message + "\n\n请手动右键程序 → 以管理员身份运行。",
                "CWriteWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}

using System.Diagnostics;
using CWriteWatcher.Core.Models;
using Microsoft.Win32;

namespace CWriteWatcher.App.Services;

/// <summary>
/// 开机自启管理（v0.2 §4.4）。
/// </summary>
/// <remarks>
/// <para><b>标准模式</b> → <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>，零提权，开关即时生效。</para>
/// <para><b>精确模式</b> → 任务计划程序 <c>/SC ONLOGON /RL HIGHEST</c>。
/// 为什么不能用 Run 键：精确模式需要管理员，用 Run 键启动会**每次开机弹 UAC**，体验灾难。
/// 任务计划以最高权限运行则登录时静默启动、不弹 UAC。</para>
/// <para><b>关键约束：切换模式时必须"先删两个再写目标"。</b>
/// 若只写新项不删旧项，两个自启项会同时存在 → 开机启动两次 →
/// 第二个实例虽然会被 Mutex 挡住，但 UAC 提示已经弹出来了。</para>
/// </remarks>
public static class AutoStartManager
{
    public const string RunValueName = "CWriteWatcher";
    public const string TaskName = "CWriteWatcher";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>移除全部自启项（Run 键 + 计划任务）。切换模式与退出时都必须调用。</summary>
    public static void RemoveAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 删除 Run 键失败：{ex.Message}");
        }

        try
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 删除计划任务失败：{ex.Message}");
        }
    }

    /// <summary>检测当前实际存在的自启项。</summary>
    public static AutoStartLocation DetectCurrent()
    {
        var hasRun = false;
        var hasTask = false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            hasRun = key?.GetValue(RunValueName) is not null;
        }
        catch { /* ignore */ }

        try
        {
            var output = RunSchtasks($"/Query /TN \"{TaskName}\"");
            hasTask = output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }

        if (hasRun && hasTask) return AutoStartLocation.RunKey; // 异常状态，交由 Apply 清理
        if (hasRun) return AutoStartLocation.RunKey;
        if (hasTask) return AutoStartLocation.ScheduledTask;
        return AutoStartLocation.None;
    }

    /// <summary>
    /// 应用自启设置。<b>内部先无条件删除两个位置，再写目标位置。</b>
    /// </summary>
    public static bool Apply(bool enabled, AutoStartLocation location, string exePath, out string error)
    {
        error = string.Empty;

        RemoveAll();

        if (!enabled) return true;

        switch (location)
        {
            case AutoStartLocation.RunKey:
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                    if (key is null)
                    {
                        error = "无法打开注册表 Run 键";
                        return false;
                    }
                    key.SetValue(RunValueName, $"\"{exePath}\" --tray", RegistryValueKind.String);
                    return true;
                }
                catch (Exception ex)
                {
                    error = "写入 Run 键失败：" + ex.Message;
                    return false;
                }
            }

            case AutoStartLocation.ScheduledTask:
            {
                // /RL HIGHEST → 以最高权限运行，登录时静默启动不弹 UAC
                var output = RunSchtasks(
                    $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F");

                if (output.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                error = "创建计划任务失败（需要管理员权限）：" + output.Trim();
                return false;
            }

            default:
                error = "未指定自启位置";
                return false;
        }
    }

    private static string RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var p = Process.Start(psi);
        if (p is null) return string.Empty;

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);

        return stdout + stderr;
    }
}

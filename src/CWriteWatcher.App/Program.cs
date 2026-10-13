using CWriteWatcher.App.Services;
using CWriteWatcher.Core.Config;
using CWriteWatcher.Core.Models;

namespace CWriteWatcher.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // 运行日志：托盘程序静默消失时，这是唯一的线索来源。
        RunLog.TrimIfTooLarge();
        RunLog.Write($"=== 启动 pid={Environment.ProcessId} 管理员={ElevationHelper.IsElevated} 参数=[{string.Join(' ', args)}] ===");
        InstallCrashHandlers();

        using var guard = new SingleInstanceGuard();

        if (!guard.IsFirstInstance)
        {
            RunLog.Write("检测到已有实例在运行，本次启动直接退出");
            var signaled = guard.SignalExistingInstance();

            MessageBox.Show(
                signaled
                    ? "CWriteWatcher 已在运行，已为你打开设置窗口。"
                    : "CWriteWatcher 已在运行（请查看系统托盘）。\n\n" +
                      "提示：如果已运行的实例是以管理员身份启动的，普通权限的本次启动无法唤起它的窗口，" +
                      "这是 Windows 的 UIPI 限制。",
                "CWriteWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var store = new ConfigStore();
        var cfg = store.Current;
        RunLog.Write($"配置：采集={cfg.CollectorMode} 触发={cfg.TriggerMode} 口径={cfg.ThresholdScope} " +
                     $"全盘={cfg.Scope.WholeDriveC} 自启={cfg.AutoStart}");

        // 启动时检查：配置里是「精确模式」，但当前不是管理员。
        // 只在"切换模式"时提示是不够的——用户下次双击启动或开机自启时，配置里已经是精确模式，
        // 程序会以普通权限启动，ETW 会话必然失败并显示"拒绝访问"，而用户并不知道该怎么办。
        if (cfg.CollectorMode == CollectorMode.Precise && !ElevationHelper.IsElevated)
        {
            RunLog.Write("配置为精确模式但当前非管理员 → 弹出提权询问");

            var answer = MessageBox.Show(
                "当前配置为「精确模式」，但它需要管理员权限。\n\n" +
                "是否立即以管理员身份重新启动程序？\n" +
                "（选择「否」将以普通权限启动，精确模式无法工作；可在设置里改回标准模式）",
                "CWriteWatcher · 需要管理员权限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            RunLog.Write($"提权询问结果：{answer}");

            // 提权成功则本进程直接结束；用户取消 UAC 则继续以普通权限启动（托盘仍可用）
            if (answer == DialogResult.Yes && ElevationHelper.RelaunchElevated())
            {
                RunLog.Write("已发起提权重启，本进程退出");
                return;
            }
        }

        using var host = new WatcherHost(store);
        using var context = new TrayApplicationContext(host, guard);

        host.Start();
        RunLog.Write($"采集器状态：{host.CollectorStatus}");

        Application.Run(context);

        RunLog.Write("消息循环结束（Application.Run 返回）");
        RunLog.Write("=== 退出 ===");
    }

    /// <summary>
    /// 装上未处理异常的兜底记录。
    /// </summary>
    /// <remarks>
    /// <c>AppDomain.UnhandledException</c> 只能记录、不能阻止进程终止；
    /// 但有了堆栈，排查"图标突然消失"就有据可依了。
    /// UI 线程上的异常用 <c>Application.ThreadException</c> 接住，避免默认的崩溃对话框。
    /// </remarks>
    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RunLog.WriteException("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => RunLog.WriteException("Application.ThreadException", e.Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RunLog.WriteException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }
}

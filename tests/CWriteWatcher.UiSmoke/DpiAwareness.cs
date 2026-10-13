using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CWriteWatcher.UiSmoke;

/// <summary>
/// 把测试宿主进程提升为 PerMonitorV2 DPI 感知。
/// </summary>
/// <remarks>
/// <para><b>为什么必须做这件事：</b>xunit 的测试跑在 <c>testhost.exe</c> 里，
/// 而进程的 DPI 感知由**宿主的清单**决定 —— 测试程序集自己的
/// <c>ApplicationHighDpiMode</c> 生成的清单在这里不起作用。
/// 结果是 <c>Control.DeviceDpi</c> 恒为 96，**在 125% 缩放的真实环境里测不出任何 DPI 问题**。</para>
/// <para>而本项目恰恰踩过这个坑：<see cref="Bitmap"/> 默认 96 DPI，
/// 布局测量却走屏幕 DC（125% 下是 120 DPI）→ 文字按 96 DPI 栅格化，
/// 比测量时小一圈也糊一圈。这个 bug 在 96 DPI 的宿主里**永远复现不了**。</para>
/// <para>用 <c>[ModuleInitializer]</c> 在**任何窗口创建之前**声明感知级别 ——
/// 一旦创建过窗口，<c>SetProcessDpiAwarenessContext</c> 就会失败。</para>
/// </remarks>
internal static class DpiAwareness
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</summary>
    private static readonly IntPtr PerMonitorV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            SetProcessDpiAwarenessContext(PerMonitorV2);
        }
        catch
        {
            // 老系统或已被宿主锁定 —— 此时测试退化为 96 DPI 运行，DPI 类断言恒真。
            // 不抛异常，避免在无法控制的机器上让整套测试失败。
        }
    }
}

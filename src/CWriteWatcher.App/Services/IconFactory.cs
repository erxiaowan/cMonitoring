using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CWriteWatcher.App.Services;

/// <summary>
/// 运行时绘制托盘图标，避免在仓库里塞二进制 .ico 文件。
/// </summary>
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon? _normal;
    private static Icon? _paused;
    private static Icon? _alert;

    public static Icon Normal => _normal ??= Build(Color.FromArgb(0x18, 0x5F, 0xA5), false, false);

    public static Icon Paused => _paused ??= Build(Color.FromArgb(0x88, 0x87, 0x80), true, false);

    public static Icon Alert => _alert ??= Build(Color.FromArgb(0xD8, 0x5A, 0x30), false, true);

    private static Icon Build(Color accent, bool paused, bool alert)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角底板
            using var board = UiKit.RoundedRect(new Rectangle(2, 2, size - 4, size - 4), 7);
            using var boardBrush = new SolidBrush(accent);
            g.FillPath(boardBrush, board);

            // 磁盘符号：一条横线 + 一个圆点
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 9, 12, 14, 3);
            g.FillRectangle(white, 9, 18, 8, 3);

            if (paused)
            {
                // 暂停：两根竖条
                using var pauseBrush = new SolidBrush(Color.White);
                g.FillRectangle(pauseBrush, 12, 12, 3, 10);
                g.FillRectangle(pauseBrush, 18, 12, 3, 10);
            }

            if (alert)
            {
                // 告警：右上角一个白点 + 红点
                using var dot = new SolidBrush(Color.White);
                g.FillEllipse(dot, 20, 4, 9, 9);
                using var inner = new SolidBrush(Color.FromArgb(0xA3, 0x2D, 0x2D));
                g.FillEllipse(inner, 22, 6, 5, 5);
            }
        }

        var hIcon = bmp.GetHicon();
        try
        {
            // 克隆一份托管 Icon，然后立刻销毁原生句柄，避免句柄泄漏
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

}

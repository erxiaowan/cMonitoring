using System.Drawing.Drawing2D;

namespace CWriteWatcher.App;

/// <summary>
/// 界面层共用的绘制与文本小工具。
/// </summary>
/// <remarks>
/// 这里的两段代码原本在多个文件里各写了一份（<c>RoundedRect</c> 在
/// <c>IconFactory</c> 与 <c>AlertPopup</c>，<c>Truncate</c> 在
/// <c>NotificationService</c> 与 <c>TrayApplicationContext</c>），
/// 而且两处 <c>Truncate</c> 的语义还不一样（一个加省略号、一个硬截断）。
/// 合并到这里，语义靠方法名区分，避免以后改一处漏一处。
/// </remarks>
internal static class UiKit
{
    /// <summary>构造圆角矩形路径。尺寸过小时退化为直角矩形（避免 AddArc 抛出）。</summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius) =>
        RoundedRectF(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);

    /// <summary>浮点版圆角矩形 —— 画小图标时需要亚像素半径。</summary>
    public static GraphicsPath RoundedRectF(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        if (d <= 0 || d > r.Width || d > r.Height)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>按字符数截断，超出时用省略号结尾（给用户看的文本）。</summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
        return max <= 1 ? "…" : s[..(max - 1)] + "…";
    }

    /// <summary>
    /// 按字符数硬截断，不加省略号。
    /// </summary>
    /// <remarks>
    /// 用于托盘提示文字 —— 它有 **127 字符的硬上限**，超了 NotifyIcon 会直接抛异常；
    /// 这种情况下不能占用一个字符去放省略号。
    /// </remarks>
    public static string TruncateHard(string s, int max) =>
        s.Length <= max ? s : s[..max];
}

using System.Windows.Forms;

namespace CWriteWatcher.App.Forms;

/// <summary>界面控件工厂。所有按钮都必须经由此处创建。</summary>
/// <remarks>
/// <para><b>为什么不能直接 <c>new Button { Text = "确定", Width = 90 }</c>：</b></para>
/// <para><see cref="Button.AutoSize"/> 的默认值是 <c>false</c>，控件高度被固定为
/// <see cref="ButtonBase"/> 的默认高度 <b>23 px</b>（这是一个 96 DPI 时代的常量）。
/// 但 Windows 的字体像素高度会随 DPI 缩放：</para>
/// <list type="bullet">
///   <item>100% DPI：Microsoft YaHei UI 9pt → <c>Font.Height = 16 px</c>，23 px 够用；</item>
///   <item>125% DPI：同一字体 → <c>Font.Height = 20 px</c>，按钮实际需要 <b>30 px</b>，
///         23 px 会把中文<b>纵向裁掉约 7 px</b>（"打开日志查看器" 只露出上半截）。</item>
/// </list>
/// <para>所以统一设置 <c>AutoSize = true</c>，让高度随字体/DPI 自适应；
/// 用 <c>MinimumSize.Width</c> 保留设计上的最小宽度，文字更长时自动加宽，
/// 顺带也避免了横向裁切。</para>
/// </remarks>
internal static class Ui
{
    /// <summary>创建一个高度自适应字体的按钮。</summary>
    /// <param name="text">按钮文字。</param>
    /// <param name="minWidth">设计上的最小宽度（px）。文字更长时按钮会自动加宽。</param>
    public static Button Btn(string text, int minWidth = 90) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(minWidth, 0),
    };
}

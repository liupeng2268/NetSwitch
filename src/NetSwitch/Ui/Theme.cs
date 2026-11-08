using System.Drawing;
using System.Drawing.Drawing2D;

namespace NetSwitch.Ui;

/// <summary>
/// Win11 主题配色与圆角路径绘制。对应旧版第 1936-1991 行
/// （<c>Initialize-UiTheme</c> / <c>Get-UiColor</c> / <c>Get-UiThemeMap</c> / <c>New-UiRoundPath</c>）。
///
/// 配色值逐字照抄旧版，保证新旧界面观感一致。
///
/// ⚠️ 与旧版的核心差异：旧版所有尺寸都要乘 <c>$script:UiScale</c>（脚本无法声明 DPI 感知，
/// 只能手工补偿）；C# 版已由 csproj 的 <c>ApplicationHighDpiMode=PerMonitorV2</c> 声明原生感知，
/// 框架会按真实 DPI 缩放，因此这里 <b>不做任何缩放乘法</b>，圆角半径就是 96 DPI 下的逻辑像素值。
/// </summary>
internal static class Theme
{
    // ── 主题色表（旧版 $script:UiTheme，逐字照抄）────────────────────────────
    public static readonly Color Bg         = Color.FromArgb(243, 243, 243);  // 窗口底色
    public static readonly Color Card       = Color.White;                    // 卡片
    public static readonly Color Border     = Color.FromArgb(229, 229, 229);  // 卡片描边
    public static readonly Color Text       = Color.FromArgb(26, 26, 26);     // 正文
    public static readonly Color TextMuted  = Color.FromArgb(96, 96, 96);     // 次要文字
    public static readonly Color Accent     = Color.FromArgb(0, 103, 192);    // Win11 强调色
    public static readonly Color AccentHot  = Color.FromArgb(25, 118, 200);
    public static readonly Color AccentDown = Color.FromArgb(0, 84, 158);
    public static readonly Color AccentSoft = Color.FromArgb(232, 240, 250);  // 选中项浅强调色
    public static readonly Color Danger     = Color.FromArgb(196, 43, 28);
    public static readonly Color DangerHot  = Color.FromArgb(253, 242, 240);
    public static readonly Color Hover      = Color.FromArgb(246, 246, 246);
    public static readonly Color Pressed    = Color.FromArgb(237, 237, 237);
    public static readonly Color LogBg      = Color.FromArgb(250, 250, 250);

    // ── 散落在旧版各处的颜色 ──────────────────────────────────────────────
    /// <summary>按钮禁用时的文字色（旧版 Set-UiButtonStyle）。</summary>
    public static readonly Color ButtonDisabledText = Color.FromArgb(160, 160, 160);

    /// <summary>Danger 按钮按下底色（旧版 Set-UiButtonStyle）。</summary>
    public static readonly Color DangerDown = Color.FromArgb(250, 231, 228);

    /// <summary>权限标签「已提权」文字色（旧版 $updateAdminLabel）。</summary>
    public static readonly Color AdminGranted = Color.FromArgb(120, 220, 160);

    /// <summary>权限标签「普通权限」文字色（旧版 $updateAdminLabel）。</summary>
    public static readonly Color AdminNormal = Color.FromArgb(240, 170, 120);

    /// <summary>列表选中项的副文字色（旧版列表 DrawItem）。</summary>
    public static readonly Color ListSubSelected = Color.FromArgb(64, 96, 132);

    /// <summary>DataGridView 表头底色 / 隔行底色（旧版 Invoke-UiTheme）。</summary>
    public static readonly Color GridHeaderBg = Color.FromArgb(250, 250, 250);
    public static readonly Color GridAlternateBg = Color.FromArgb(252, 252, 252);

    // ── 圆角半径（旧版 New-UiRoundPath 的 Radius 实参）────────────────────
    /// <summary>按钮圆角半径。</summary>
    public const int ButtonRadius = 4;

    /// <summary>卡片（原 GroupBox）圆角半径。</summary>
    public const int CardRadius = 8;

    // ── 字体工厂（旧版 New-FormFont / New-MonoFont）────────────────────────
    // ⚠️ 这里绝不能乘任何缩放系数：磅（pt）是物理单位，DPI 感知下 9pt 自然渲染成
    //    对应像素高度；旧版注释已明确记录「再乘一次会把字顶破按钮」。
    private const string UiFontFamily = "Microsoft YaHei UI";
    private const string FallbackFamily = "Microsoft Sans Serif";
    private const string MonoFamily = "Consolas";

    /// <summary>界面字体。对应旧版 <c>New-FormFont</c>（失败回退 Microsoft Sans Serif）。</summary>
    public static Font UiFont(float size = 9f, FontStyle style = FontStyle.Regular)
    {
        try
        {
            return new Font(UiFontFamily, size, style);
        }
        catch
        {
            return new Font(FallbackFamily, size, style);
        }
    }

    /// <summary>等宽字体。对应旧版 <c>New-MonoFont</c>。调用方负责 Dispose。</summary>
    public static Font MonoFont(float size = 9f) => new(MonoFamily, size);

    /// <summary>
    /// 圆角矩形路径。对应旧版 <c>New-UiRoundPath</c>。
    /// 半径不能超过短边的一半，否则弧会互相穿插画出畸形路径。
    ///
    /// ⚠️ 旧版此处会乘 <c>$script:UiScale</c>；C# 版由框架处理 DPI，<b>不做任何缩放</b>。
    /// </summary>
    public static GraphicsPath CreateRoundPath(Rectangle rect, int radius)
    {
        var d = radius * 2;

        // 半径不能超过短边的一半
        if (d > rect.Height) d = rect.Height;
        if (d > rect.Width) d = rect.Width;
        if (d < 0) d = 0;

        var path = new GraphicsPath();
        try
        {
            if (d == 0)
            {
                path.AddRectangle(rect);
            }
            else
            {
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
        }
        catch
        {
            path.Dispose();
            var plain = new GraphicsPath();
            plain.AddRectangle(rect);
            return plain;
        }

        return path;
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace NetSwitch.Ui;

/// <summary>
/// 绘制诊断计数器（仅开发期用）。
///
/// 用来回答「点别处为什么会闪」这类问题：闪烁的本质是短时间内同一块区域被反复
/// 「擦背景 + 重画」。只靠肉眼判断改好没好容易反复，这里改成数数 ——
/// 切换焦点前后看 <see cref="ListRedraws"/> 涨了多少，就能定量判断。
///
/// 统计口径：
///   • <see cref="ListRedraws"/>：配置集列表 DrawItem 被调用的次数
///   • <see cref="ContainerPaints"/>：自绘容器的 Paint 次数
/// 正常情况下，切换一次焦点只应带来个位数增长；若几十上百，说明有重复重绘。
/// </summary>
internal static class PaintStats
{
    private static int _listRedraws;
    private static int _containerPaints;

    public static bool Enabled { get; set; }

    public static int ListRedraws => _listRedraws;
    public static int ContainerPaints => _containerPaints;

    public static void CountListRedraw()
    {
        if (Enabled) Interlocked.Increment(ref _listRedraws);
    }

    public static void CountContainerPaint()
    {
        if (Enabled) Interlocked.Increment(ref _containerPaints);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _listRedraws, 0);
        Interlocked.Exchange(ref _containerPaints, 0);
    }

    public static string Snapshot()
        => $"listRedraws={_listRedraws} containerPaints={_containerPaints}";
}

/// <summary>
/// 自绘能力探针：验证「双缓冲 / UserPaint 标志是否真的设置成功」。
///
/// 反射调 protected 的 SetStyle 失败是静默的，光看代码无法确认是否生效，
/// 因此这里把 <c>Control.GetStyle</c> 的结果读回来核对。
/// </summary>
internal static class PaintProbe
{
    public static string Describe(Control control)
    {
        try
        {
            var method = typeof(Control).GetMethod(
                "GetStyle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (method is null) return $"{control.GetType().Name}: GetStyle 不可用";

            const int AllPaintingInWmPaint = 0x00000020;
            const int OptimizedDoubleBuffer = 0x00010000;
            const int UserPaint = 0x00008000;

            bool Has(int flag) => (bool)method.Invoke(control, new object[] { flag })!;

            return $"{control.GetType().Name,-14} " +
                   $"doubleBuffer={(Has(OptimizedDoubleBuffer) ? "Y" : "N")} " +
                   $"userPaint={(Has(UserPaint) ? "Y" : "N")} " +
                   $"allInWmPaint={(Has(AllPaintingInWmPaint) ? "Y" : "N")}";
        }
        catch (Exception ex)
        {
            return $"{control.GetType().Name}: 探测失败 {ex.Message}";
        }
    }

    /// <summary>把整棵控件树的双缓冲状态导成文本，供开发期核对哪些控件漏设了。</summary>
    public static string DumpAll(Control root)
    {
        var sb = new System.Text.StringBuilder();
        Walk(root, 0, sb);
        return sb.ToString();
    }

    private static void Walk(Control control, int depth, System.Text.StringBuilder sb)
    {
        sb.Append(' ', depth * 2).AppendLine(Describe(control));
        foreach (Control child in control.Controls) Walk(child, depth + 1, sb);
    }
}

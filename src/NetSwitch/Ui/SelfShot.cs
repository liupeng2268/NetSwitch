using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetSwitch.Ui;

/// <summary>
/// 界面自截图 —— 仅用于开发期排查布局问题。
///
/// 用法：<c>NetSwitch.exe --shot &lt;输出目录&gt;</c>
/// 程序启动后延迟一会儿（等布局与首次绘制稳定），把主窗体按<b>客户区</b>截成 PNG 后自动退出。
///
/// 为什么要有它：GUI 布局问题（控件被遮挡、错位、溢出）只能靠看，
/// 让用户反复截图既慢又容易遗漏。有了自截图，改完代码立刻能看结果。
/// 这在旧版 PowerShell 时代做不到（那时靠 diag-shot.ps1 外部截屏，还踩过 DPI 虚拟化的坑）。
/// </summary>
internal static class SelfShot
{
    /// <summary>
    /// 延迟截图并退出。必须在 <c>Application.Run</c> 之后调用（窗体已显示）。
    /// </summary>
    public static async void CaptureAndExit(Form form, string outputDir, int delayMs = 1200)
    {
        try
        {
            await Task.Delay(delayMs);

            Directory.CreateDirectory(outputDir);

            var path = Path.Combine(outputDir, $"{SanitizeName(form.Text)}.png");

            // 用窗体的 DrawToBitmap 而不是屏幕截图：
            //   • 不受 DPI 虚拟化影响（旧版 diag-shot 就栽在这上面，只截到左上角）
            //   • 不需要窗口在最前，也不会截到别的窗口
            var rect = form.ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                rect = new Rectangle(0, 0, form.Width, form.Height);
            }

            using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(bmp, rect);
            bmp.Save(path, ImageFormat.Png);

            File.WriteAllText(
                Path.Combine(outputDir, "shot-info.txt"),
                $"form={form.Text}\r\n" +
                $"client={rect.Width}x{rect.Height}\r\n" +
                $"dpi={form.DeviceDpi}\r\n" +
                $"scale={(form.DeviceDpi / 96.0):0.00}\r\n" +
                $"autoscale={form.AutoScaleMode}\r\n");

            // 再导出控件树，便于核对坐标
            File.WriteAllText(
                Path.Combine(outputDir, "layout.txt"),
                DumpLayout(form));
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(outputDir, "shot-error.txt"),
                    ex.ToString());
            }
            catch { }
        }
        finally
        {
            try { form.Close(); } catch { }
            Application.Exit();
        }
    }

    /// <summary>
    /// 双缓冲自检：把控件树里每个控件的 SetStyle 实际状态读回来，写盘供核对。
    ///
    /// 为什么需要它：反射调 protected 的 <c>SetStyle</c> 失败是<b>静默</b>的，
    /// 只看代码无法确认「双缓冲到底设上了没有」。这里用 <c>GetStyle</c> 反向验证，
    /// 并把结果落盘 —— 漏设的控件一眼可见。
    /// </summary>
    public static async void CheckDoubleBufferAndExit(Form form, string outputDir, int delayMs = 1200)
    {
        try
        {
            await Task.Delay(delayMs);

            Directory.CreateDirectory(outputDir);

            PaintStats.Enabled = true;
            PaintStats.Reset();

            var before = PaintStats.Snapshot();

            // 模拟「点别处」：让列表失焦再拿回，这一动作在修复前会引发整表重绘闪烁
            form.ActiveControl = null;
            await Task.Delay(120);
            form.ActiveControl = form.Controls.Count > 0 ? form.Controls[0] : null;
            await Task.Delay(220);

            var after = PaintStats.Snapshot();

            var report = new System.Text.StringBuilder();
            report.AppendLine("# 双缓冲与重绘自检");
            report.AppendLine($"form dpi={form.DeviceDpi}");
            report.AppendLine();
            report.AppendLine("## 焦点切换前后的绘制计数");
            report.AppendLine($"before: {before}");
            report.AppendLine($"after : {after}");
            report.AppendLine();
            report.AppendLine("## 每个控件的 SetStyle 实际状态");
            report.AppendLine(PaintProbe.DumpAll(form));

            File.WriteAllText(Path.Combine(outputDir, "doublebuffer.txt"), report.ToString());
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(outputDir, "doublebuffer-error.txt"), ex.ToString());
            }
            catch { }
        }
        finally
        {
            try { form.Close(); } catch { }
            Application.Exit();
        }
    }

    private static string SanitizeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "main";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        return name.Length > 60 ? name[..60] : name;
    }

    /// <summary>导出控件树：缩进表示层级，标出坐标/尺寸/字体，越界的加标记。</summary>
    private static string DumpLayout(Control root)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# NetSwitch 布局导出");
        builder.AppendLine($"# form={root.Width}x{root.Height} client={root.ClientSize.Width}x{root.ClientSize.Height} dpi={root.DeviceDpi}");
        builder.AppendLine();
        Walk(root, 0, builder);
        return builder.ToString();
    }

    private static void Walk(Control parent, int depth, System.Text.StringBuilder builder)
    {
        foreach (Control c in parent.Controls)
        {
            var indent = new string(' ', depth * 2);
            var text = c.Text.Replace("\r", "").Replace("\n", " ");
            if (text.Length > 24) text = text[..24] + "…";

            var overflow = "";
            if (c.Right > parent.ClientSize.Width + 1 || c.Bottom > parent.ClientSize.Height + 1)
            {
                overflow = "   <<< OVERFLOW";
            }

            builder.AppendLine(
                $"{indent}- {c.GetType().Name} l={c.Left} t={c.Top} w={c.Width} h={c.Height} " +
                $"anchor={c.Anchor} font={c.Font.Size:0.#}pt text=\"{text}\"{overflow}");

            if (c.HasChildren) Walk(c, depth + 1, builder);
        }
    }
}

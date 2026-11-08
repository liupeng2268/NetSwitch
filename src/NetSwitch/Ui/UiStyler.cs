using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Windows.Forms;

namespace NetSwitch.Ui;

/// <summary>
/// 主题套用器。对应旧版 <c>Invoke-UiTheme</c> / <c>Set-UiButtonStyle</c> /
/// <c>ConvertTo-UiCard</c> / <c>Enable-UiDoubleBuffer</c>（旧版第 1996-2356 行）。
///
/// 设计：递归遍历控件树统一套用，**改动集中在主题层，各处布局坐标一行不动**。
///
/// 两个从旧版踩坑记录里继承下来的细节，务必保留：
///   1. <c>Paint</c> 事件在控件自身文字绘制「之后」触发，自绘背景会盖掉按钮文字，
///      因此必须在 Paint 里自己 <c>DrawString</c> 重画文字。
///   2. <c>DrawString</c> 必须显式构造 <c>RectangleF</c>，否则会选错重载。
///
/// ⚠️ 本类<b>不做任何 DPI 缩放</b>：csproj 已声明 <c>PerMonitorV2</c>，
/// 坐标与字号都按逻辑像素给出，框架负责换算。
/// </summary>
internal static class UiStyler
{
    /// <summary>
    /// 按当前 DPI 缩放整个窗体（含全部子控件）。
    ///
    /// <para>
    /// 为什么不用 <c>AutoScaleMode.Dpi</c> / <c>Control.Scale()</c>：
    /// 本项目的控件是在构造函数里逐个 new 出来并 Add 进去的，而 WinForms 的自动缩放
    /// 早在构造期就执行完了 —— 那些控件根本不在它的视野里。
    /// </para>
    /// <para>
    /// 实测 <c>Control.Scale(SizeF)</c> 也不可靠：它会缩放 Label 的宽高，却<b>不缩放
    /// Button 的宽度</b>，连 Form 自身的宽度都没变（1180 仍是 1180），
    /// 结果就是「位置全对、宽度没变」→ 按钮文字被截、整体横向溢出。
    /// </para>
    /// <para>
    /// 因此这里采取确定性做法：<b>自己记录原始 Bounds，全部乘系数后重设</b>。
    /// 关键顺序是先把 Anchor 全部清空再缩放 —— 否则父容器一改尺寸，WinForms 会按
    /// 仍生效的 Anchor 再调整一次子控件，造成「放大两次」或「位置乱飞」，
    /// 这正是旧版 PowerShell 版本踩过三轮的坑。
    /// </para>
    /// <para>
    /// <b>字体绝不缩放</b>：磅（pt）是物理单位，PerMonitorV2 下 9pt 本来就渲染成 18px；
    /// 再乘 1.5 会变成 13.5pt，字顶破按钮。
    /// </para>
    /// </summary>
    public static void ScaleForDpi(Form form)
    {
        var scale = form.DeviceDpi / 96.0;
        if (scale <= 1.001) return;

        // 1) 快照全部控件（含窗体自身）的原始坐标、尺寸与锚点
        var items = new List<ScaleItem>();
        CollectForScale(form, items);

        // 2) 停掉布局，并清空锚点 —— 保证缩放过程中没人来「二次调整」
        form.SuspendLayout();
        foreach (var item in items)
        {
            item.Anchor = item.Control.Anchor;
            item.Control.Anchor = AnchorStyles.None;

            // FlowLayoutPanel 的子控件由流式布局接管位置，这里只缩放尺寸
            item.IsFlowChild = item.Control.Parent is FlowLayoutPanel;
        }

        // 3) 逐个重设坐标与尺寸
        foreach (var item in items)
        {
            if (!item.IsFlowChild)
            {
                item.Control.Location = new Point(
                    (int)Math.Round(item.Bounds.X * scale),
                    (int)Math.Round(item.Bounds.Y * scale));
            }

            item.Control.Size = new Size(
                (int)Math.Round(item.Bounds.Width * scale),
                (int)Math.Round(item.Bounds.Height * scale));

            // 流式布局的间距也按比例放大，否则按钮会挤在一起
            if (item.IsFlowChild)
            {
                var m = item.Control.Margin;
                item.Control.Margin = new Padding(
                    (int)Math.Round(m.Left * scale),
                    (int)Math.Round(m.Top * scale),
                    (int)Math.Round(m.Right * scale),
                    (int)Math.Round(m.Bottom * scale));
            }
        }

        // 4) 窗体自身的尺寸与最小尺寸（快照里已含 form，但 MinimumSize 不在 Bounds 中）
        form.MinimumSize = new Size(
            (int)Math.Round(form.MinimumSize.Width * scale),
            (int)Math.Round(form.MinimumSize.Height * scale));

        // 5) 还原锚点，恢复布局
        foreach (var item in items)
        {
            item.Control.Anchor = item.Anchor;
        }
        form.ResumeLayout(true);

        // 6) 非 Control.Size 范畴的度量单独处理
        RescaleNonControlMetrics(form, scale);
    }

    private sealed class ScaleItem
    {
        public required Control Control { get; init; }
        public required Rectangle Bounds { get; init; }
        public bool IsFlowChild { get; set; }
        public AnchorStyles Anchor { get; set; }
    }

    private static void CollectForScale(Control root, List<ScaleItem> sink)
    {
        sink.Add(new ScaleItem { Control = root, Bounds = root.Bounds });

        foreach (Control child in root.Controls)
        {
            CollectForScale(child, sink);
        }
    }

    /// <summary>
    /// 缩放不属于 <c>Control.Size</c> 的度量：列表行高、表格行高与列宽、卡片标题区。
    /// 不补这一步，150% 缩放下两行列表文字会挤在一起、表格行高不足导致内容被裁。
    /// </summary>
    private static void RescaleNonControlMetrics(Control root, double scale)
    {
        switch (root)
        {
            case ListBox listBox when listBox.DrawMode == DrawMode.OwnerDrawFixed:
                listBox.ItemHeight = (int)Math.Round(listBox.ItemHeight * scale);
                break;

            case DataGridView grid:
                grid.RowTemplate.Height = (int)Math.Round(grid.RowTemplate.Height * scale);
                grid.ColumnHeadersHeight = (int)Math.Round(grid.ColumnHeadersHeight * scale);
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    column.Width = (int)Math.Round(column.Width * scale);
                }
                break;
        }

        foreach (Control child in root.Controls)
        {
            RescaleNonControlMetrics(child, scale);
        }
    }

    /// <summary>递归套用主题到整棵控件树。</summary>
    public static void Apply(Control root)
    {
        if (root is null) return;

        EnableDoubleBuffer(root);

        if (root is Form form)
        {
            form.BackColor = Theme.Bg;
            form.Font = Theme.UiFont(9f);
        }

        foreach (Control child in root.Controls)
        {
            StyleControl(child);
        }
    }

    private static void StyleControl(Control control)
    {
        // 每个控件都要开双缓冲，不只是根窗体。
        // 这里曾漏调过 —— 结果 Form 与 Button 有双缓冲，ListBox 与各 Panel 容器却没有，
        // 点别处导致列表失焦重绘时就会看到整块闪一下。必须在类型分派【之前】统一处理。
        EnableDoubleBuffer(control);

        switch (control)
        {
            case GroupBox groupBox:
                // GroupBox 无法自绘圆角，旧版的做法是整体换成自绘 Panel 卡片
                ReplaceGroupBoxWithCard(groupBox);
                return;

            case Button button:
                StyleButton(button);
                break;

            case RichTextBox rich:
                rich.BackColor = Theme.LogBg;
                rich.ForeColor = Theme.Text;
                rich.BorderStyle = BorderStyle.None;
                rich.Font = Theme.MonoFont(9f);
                break;

            case TextBox textBox:
                textBox.BackColor = Theme.LogBg;
                textBox.ForeColor = Theme.Text;
                if (textBox.BorderStyle != BorderStyle.FixedSingle) textBox.BorderStyle = BorderStyle.None;
                if (textBox.Multiline) textBox.Font = Theme.MonoFont(9f);
                break;

            case DataGridView grid:
                StyleGrid(grid);
                break;

            case Label label:
                // 只把纯黑字改成主题文字色，白色标题保持不动
                if (label.ForeColor == Color.Black) label.ForeColor = Theme.Text;
                break;

            case ListBox listBox:
                listBox.ForeColor = Theme.Text;
                // 保留创建时设定的背景色（主窗体的配置集列表用 Bg 灰底，
                // 与左侧面板同色才不突兀；一律强制成 Card 白底会显得是一块白斑）。
                // 只有仍处于系统默认白底时才改成 Card。
                if (listBox.BackColor == SystemColors.Window) listBox.BackColor = Theme.Card;
                listBox.BorderStyle = BorderStyle.None;
                break;

            case CheckBox checkBox:
                checkBox.ForeColor = Theme.TextMuted;
                checkBox.Cursor = Cursors.Hand;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel:
                // 容器本身不改色（由创建时设定），只递归处理子控件
                break;
        }

        if (control.HasChildren) Apply(control);
    }

    // ── 按钮自绘 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 圆角按钮。对应旧版 <c>Set-UiButtonStyle</c>。
    /// 角色由控件的 <c>Tag</c> 决定（Primary / Secondary / Danger）。
    /// </summary>
    private static void StyleButton(Button button)
    {
        var role = ResolveRole(button);

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        button.BackColor = themeBack(role);
        button.ForeColor = themeFore(role);
        button.Font = Theme.UiFont(9f);
        EnableDoubleBuffer(button);

        var hovered = false;
        var pressed = false;

        button.MouseEnter += (_, _) => { hovered = true; button.Invalidate(); };
        button.MouseLeave += (_, _) => { hovered = false; pressed = false; button.Invalidate(); };
        button.MouseDown += (_, _) => { pressed = true; button.Invalidate(); };
        button.MouseUp += (_, _) => { pressed = false; button.Invalidate(); };

        button.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 内缩 1px，宽高减 3（旧版 Set-UiButtonStyle 的取值）
            var rect = new Rectangle(1, 1, button.Width - 3, button.Height - 3);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            var enabled = button.Enabled;
            var (fill, border, textColor) = ResolveColors(role, enabled, hovered, pressed);

            using (var path = Theme.CreateRoundPath(rect, Theme.ButtonRadius))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
                if (border != Color.Empty)
                {
                    using var pen = new Pen(border, 1f);
                    g.DrawPath(pen, path);
                }
            }

            // 必须在这里重画文字：Paint 在控件自身文字之后触发，背景已把文字盖掉
            var text = button.Text;
            if (string.IsNullOrEmpty(text)) return;

            var flags = TextFormatFlags.HorizontalCenter
                      | TextFormatFlags.VerticalCenter
                      | TextFormatFlags.EndEllipsis
                      | TextFormatFlags.NoPrefix;

            var textRect = new RectangleF(2, 2, button.Width - 4, button.Height - 4);
            TextRenderer.DrawText(g, text, button.Font, Rectangle.Round(textRect), textColor, flags);
        };
    }

    private static UiRole ResolveRole(Control control)
    {
        if (control.Tag is UiRole role) return role;

        // 兼容 Tag 写成字符串的情况
        if (control.Tag is string text)
        {
            return text.ToLowerInvariant() switch
            {
                "primary" => UiRole.Primary,
                "danger" => UiRole.Danger,
                _ => UiRole.Secondary,
            };
        }

        return UiRole.Secondary;
    }

    private static Color themeBack(UiRole role) => role switch
    {
        UiRole.Primary => Theme.Accent,
        UiRole.Danger => Theme.Card,
        _ => Theme.Card,
    };

    private static Color themeFore(UiRole role) => role switch
    {
        UiRole.Primary => Color.White,
        UiRole.Danger => Theme.Danger,
        _ => Theme.Text,
    };

    private static (Color Fill, Color Border, Color Text) ResolveColors(
        UiRole role, bool enabled, bool hovered, bool pressed)
    {
        if (!enabled)
        {
            return role switch
            {
                UiRole.Primary => (Theme.Pressed, Color.Empty, Theme.ButtonDisabledText),
                UiRole.Danger => (Theme.Card, Theme.Border, Theme.ButtonDisabledText),
                _ => (Theme.Card, Theme.Border, Theme.ButtonDisabledText),
            };
        }

        return role switch
        {
            UiRole.Primary => (
                pressed ? Theme.AccentDown : hovered ? Theme.AccentHot : Theme.Accent,
                Color.Empty,
                Color.White),

            UiRole.Danger => (
                pressed ? Theme.DangerDown : hovered ? Theme.DangerHot : Theme.Card,
                Theme.Border,
                Theme.Danger),

            _ => (
                pressed ? Theme.Pressed : hovered ? Theme.Hover : Theme.Card,
                Theme.Border,
                Theme.Text),
        };
    }

    // ── GroupBox → 圆角卡片 ───────────────────────────────────────────────

    /// <summary>
    /// 把 GroupBox 换成自绘圆角白卡片。对应旧版 <c>ConvertTo-UiCard</c>。
    ///
    /// 做法：新建同位置同尺寸的 Panel，写一个能画标题的派生类，
    /// 再把原 GroupBox 的子控件转移过去，最后移除原控件。
    ///
    /// 关键：<see cref="CardPanel"/> 重写了 <c>DisplayRectangle</c>，把标题区让出来。
    /// 因此子控件的坐标<b>不需要做任何平移</b> —— 它们本来就相对「标题下方」定位，
    /// 换容器后原点语义完全相同。旧版 PowerShell 的 <c>ConvertTo-UiCard</c> 也是这个思路。
    /// </summary>
    private static void ReplaceGroupBoxWithCard(GroupBox groupBox)
    {
        var parent = groupBox.Parent;
        if (parent is null) return;

        var card = new CardPanel
        {
            Text = groupBox.Text,
            Location = groupBox.Location,
            Size = groupBox.Size,
            Anchor = groupBox.Anchor,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            TabIndex = groupBox.TabIndex,
        };

        var children = new List<Control>();
        foreach (Control child in groupBox.Controls) children.Add(child);

        groupBox.Controls.Clear();
        foreach (var child in children)
        {
            card.Controls.Add(child);
        }

        var index = parent.Controls.GetChildIndex(groupBox);
        parent.Controls.Remove(groupBox);
        parent.Controls.Add(card);
        parent.Controls.SetChildIndex(card, index);

        groupBox.Dispose();

        // 卡片内的子控件也要套用主题
        Apply(card);
    }

    /// <summary>
    /// 自绘圆角卡片：白底 + 1px 描边 + 左上角标题。
    /// 对应旧版 <c>ConvertTo-UiCard</c> 的 Paint 逻辑：半径 8、标题 10pt Bold、
    /// 内边距 X=14 / Y=5、标题高 22。
    /// </summary>
    private sealed class CardPanel : Panel
    {
        /// <summary>标题区高度（96 DPI 下的逻辑值，实际按 DeviceDpi 换算）。</summary>
        public const int TitleHeight = 26;

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
        }

        /// <summary>
        /// 让出顶部标题区 —— 这是替代 GroupBox 的关键。
        ///
        /// GroupBox 内部就是重写 <c>DisplayRectangle</c> 把标题那一条挖掉，子控件的
        /// Location 因此相对「标题下方」定位。Panel 的 DisplayRectangle 默认等于整个客户区，
        /// 所以必须在 <b>布局之前</b>挖掉同样的高度，子控件坐标才能原样复用、不压住标题。
        ///
        /// 这里同时承担 Anchor/Dock 的基准 —— 只平移子控件 Y 是不够的：
        /// Anchor=Bottom 的控件会按 DisplayRectangle 的高度差来算拉伸量。
        /// </summary>
        public override Rectangle DisplayRectangle
        {
            get
            {
                var rect = base.DisplayRectangle;
                var caption = CaptionHeight;
                if (rect.Height <= caption) return rect;
                return new Rectangle(rect.X, rect.Y + caption, rect.Width, rect.Height - caption);
            }
        }

        /// <summary>标题区实际高度，按当前 DPI 换算（150% 缩放下为 39px）。</summary>
        private int CaptionHeight
        {
            get
            {
                var scale = DeviceDpi / 96.0;
                return (int)Math.Round(TitleHeight * scale);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (var path = Theme.CreateRoundPath(rect, Theme.CardRadius))
            using (var brush = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.Border, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            if (string.IsNullOrEmpty(Text)) return;

            // 标题：10pt Bold、内边距 X=14 / Y=5、标题高 22（均按 DPI 换算）
            var scale = DeviceDpi / 96.0;
            using var titleFont = Theme.UiFont(10f, FontStyle.Bold);
            var titleRect = new RectangleF(
                (float)(14 * scale),
                (float)(5 * scale),
                (float)Math.Max(0, Width - 28 * scale),
                (float)(22 * scale));
            TextRenderer.DrawText(
                g, Text, titleFont, Rectangle.Round(titleRect), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // ── DataGridView ──────────────────────────────────────────────────────

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Theme.Card;
        grid.GridColor = Theme.Border;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.GridHeaderBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.GridHeaderBg;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.UiFont(9f, FontStyle.Bold);

        grid.DefaultCellStyle.BackColor = Theme.Card;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.DefaultCellStyle.Font = Theme.UiFont(9f);

        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridAlternateBg;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Text;
    }

    // ── 双缓冲 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 打开双缓冲消除自绘闪烁。对应旧版 <c>Enable-UiDoubleBuffer</c>。
    /// <c>SetStyle</c> 是 protected，只能靠反射调用；失败就原样放过，不影响功能。
    /// </summary>
    /// <summary>
    /// 打开双缓冲，消除自绘控件的闪烁。
    ///
    /// 背景（旧版踩过的坑）：卡片与列表都是自绘，而 WinForms 的 Panel / ListBox
    /// 默认不开双缓冲 —— 每次重绘先擦背景再画内容，点一下就能看到整块闪一下。
    ///
    /// 本方法按控件性质分两条路：
    ///   • <b>自绘控件</b>（Panel / 自绘 Button / 自绘 ListBox）：
    ///     设 AllPaintingInWmPaint | OptimizedDoubleBuffer | UserPaint —— 三者缺一不可，
    ///     少了 UserPaint，WM_ERASEBKGND 仍会先擦一遍背景，闪烁照旧。
    ///   • <b>原生控件</b>（标准 ListBox / TextBox 等）：
    ///     <b>只</b>设 OptimizedDoubleBuffer，<b>不能</b>加 UserPaint ——
    ///     原生控件把绘制委托给系统的窗口类，强行标记 UserPaint 会让 WinForms
    ///     绕过原生绘制路径，反而更容易闪、甚至显示异常。
    /// </summary>
    private static void EnableDoubleBuffer(Control control)
    {
        try
        {
            var method = typeof(Control).GetMethod(
                "SetStyle",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (method is null) return;

            const int AllPaintingInWmPaint = 0x00000020;
            const int OptimizedDoubleBuffer = 0x00010000;
            const int UserPaint = 0x00008000;
            const int Opaque = 0x00000004;

            var isCustomPainted =
                control is Panel
                || control is Button
                || (control is ListBox lb && lb.DrawMode != DrawMode.Normal);

            var flags = isCustomPainted
                ? AllPaintingInWmPaint | OptimizedDoubleBuffer | UserPaint | Opaque
                : OptimizedDoubleBuffer;

            method.Invoke(control, new object[] { flags, true });

            // Panel.DoubleBuffered 也是 protected，同样靠反射打开，双保险
            if (control is Panel)
            {
                try
                {
                    var prop = typeof(Control).GetProperty(
                        "DoubleBuffered",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    prop?.SetValue(control, true);
                }
                catch
                {
                    // 属性不可写时忽略
                }
            }
        }
        catch
        {
            // 反射失败不影响功能，只是可能有轻微闪烁
        }
    }
}

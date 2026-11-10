using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 主界面。逐控件复刻旧版 <c>Show-MainForm</c>（第 3169-3865 行）。
///
/// 布局策略：
///   • 三栏绝对坐标（header / side / right）+ Anchor，与旧版像素级一致，便于对比验证
///   • 左侧按钮区与右侧操作区改用 <see cref="FlowLayoutPanel"/> ——
///     这两处是旧版最容易溢出的部分，用流式布局可彻底摆脱手工算坐标
///
/// DPI：<b>不做任何缩放乘法</b>。csproj 已声明 PerMonitorV2，坐标即逻辑像素。
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppServices _services;

    private readonly Panel _header = new();
    private readonly Label _lblTitle = new();
    private readonly Label _lblConfig = new();
    private readonly Label _lblAdmin = new();
    private readonly Button _btnElevate = new();
    private readonly Panel _headerLine = new();

    private readonly Panel _side = new();
    private readonly Label _lblSide = new();
    private readonly ListBox _lstProfiles = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnCopy = new();
    private readonly Button _btnEdit = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnUp = new();
    private readonly Button _btnDown = new();
    private readonly Button _btnImport = new();
    private readonly CheckBox _chkIncludeVirtual = new();

    private readonly Panel _right = new();
    private readonly GroupBox _grpStatus = new();
    private readonly TextBox _txtStatus = new();
    private readonly GroupBox _grpDetail = new();
    private readonly TextBox _txtDetail = new();
    private readonly Panel _action = new();
    private readonly Button _btnApply = new();
    private readonly Button _btnPreview = new();
    private readonly Button _btnShortcut = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnBackup = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnOpenFile = new();
    private readonly Button _btnCopyLog = new();
    private readonly GroupBox _grpLog = new();
    private readonly RichTextBox _txtLog = new();

    // 列表自绘用的字体与画刷（只需创建一次，随窗体释放）
    // 绝不能在 DrawItem 里 new —— 每次重绘都创建销毁 GDI 句柄会造成可见闪烁。
    private readonly Font _listFontNormal = Theme.UiFont(10f, FontStyle.Regular);
    private readonly Font _listFontBold = Theme.UiFont(10f, FontStyle.Bold);
    private readonly Font _listSubFont = Theme.UiFont(8f);
    private readonly SolidBrush _listBgBrush = new(Theme.Bg);
    private readonly SolidBrush _listSelBlockBrush = new(Theme.AccentSoft);
    private readonly SolidBrush _listSelBarBrush = new(Theme.Accent);

    public MainForm(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        BuildForm();
        BuildHeader();
        BuildSide();
        BuildRight();

        UiStyler.Apply(this);

        // 全部控件建完后统一按 DPI 缩放一次。
        // 不能用 AutoScaleMode.Dpi 自动缩放：WinForms 在构造函数里就完成了 PerformAutoScale，
        // 之后添加的控件不会再被处理，结果就是「字变大、控件不变」。
        UiStyler.ScaleForDpi(this);

        Load += (_, _) => RefreshAll();
        Shown += OnShown;
    }

    // ── 窗体骨架 ──────────────────────────────────────────────────────────

    private void BuildForm()
    {
        Text = $"NetSwitch {UiContract.Version}  -  内网 IP / 网关 一键切换";

        // ── 高 DPI 正确姿势 ──────────────────────────────────────────────
        // ApplicationHighDpiMode=PerMonitorV2 只负责「进程声明感知」，让文字按真实 DPI
        // 渲染成更多像素；它<b>不会</b>把控件尺寸放大。若 AutoScaleMode 留在 Inherit/None，
        // 就会出现本项目的经典症状：字变大、按钮不变 → 文字顶破按钮、列表项挤成一团。
        //
        // 正确做法：先定 AutoScaleMode = Dpi 与 AutoScaleDimensions，再设 Size。
        // WinForms 会按 当前DPI/设计DPI 自动缩放所有子控件的坐标与尺寸。
        // 设计基准固定为 96 DPI（旧版手工稿的坐标系），因此这里写 (96, 96)。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        // Size 用设计值给出，交由框架按 DPI 放大；MinimumSize 同理。
        Size = new Size(1180, 760);
        MinimumSize = new Size(1180, 720);

        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.UiFont(9f);
        DoubleBuffered = true;

        // 顶栏、左栏、右栏作为三个顶层容器直接挂在 Form 上
        _header.Dock = DockStyle.None;
    }

    private void BuildHeader()
    {
        _header.Location = new Point(0, 0);
        _header.Size = new Size(1180, 70);
        _header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _header.BackColor = Theme.Bg;

        _lblTitle.Text = "NetSwitch";
        _lblTitle.Location = new Point(20, 13);
        _lblTitle.Size = new Size(520, 30);
        _lblTitle.Font = Theme.UiFont(15f, FontStyle.Bold);
        _lblTitle.ForeColor = Theme.Text;

        _lblConfig.Location = new Point(22, 45);
        _lblConfig.Size = new Size(620, 18);
        _lblConfig.Font = Theme.UiFont(8f);
        _lblConfig.ForeColor = Theme.TextMuted;

        _lblAdmin.Location = new Point(700, 18);
        _lblAdmin.Size = new Size(300, 26);
        _lblAdmin.Font = Theme.UiFont(9f, FontStyle.Bold);
        _lblAdmin.TextAlign = ContentAlignment.MiddleRight;

        _btnElevate.Text = "以管理员身份重启";
        _btnElevate.Location = new Point(1010, 16);
        _btnElevate.Size = new Size(150, 34);
        _btnElevate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnElevate.Tag = UiRole.Primary;
        _btnElevate.Visible = false;
        _btnElevate.Click += OnElevateClick;

        _headerLine.Location = new Point(0, 69);
        _headerLine.Size = new Size(1180, 1);
        _headerLine.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _headerLine.BackColor = Theme.Border;

        _header.Controls.AddRange(new Control[]
        {
            _lblTitle, _lblConfig, _lblAdmin, _btnElevate, _headerLine,
        });

        Controls.Add(_header);
    }

    private void BuildSide()
    {
        _side.Location = new Point(12, 82);
        _side.Size = new Size(268, 630);
        _side.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _side.BackColor = Theme.Bg;

        _lblSide.Text = "配置集";
        _lblSide.Location = new Point(2, 0);
        _lblSide.Size = new Size(200, 20);
        _lblSide.Font = Theme.UiFont(9f, FontStyle.Bold);
        _lblSide.ForeColor = Theme.TextMuted;

        // 列表：自绘两行（主行名称 + 副行备注）
        _lstProfiles.Location = new Point(2, 24);
        _lstProfiles.Size = new Size(262, 400);
        _lstProfiles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lstProfiles.Font = Theme.UiFont(10f);
        _lstProfiles.DrawMode = DrawMode.OwnerDrawFixed;
        _lstProfiles.ItemHeight = 44;
        _lstProfiles.BorderStyle = BorderStyle.None;
        _lstProfiles.BackColor = Theme.Bg;
        _lstProfiles.IntegralHeight = false;
        _lstProfiles.DrawItem += OnDrawProfileItem;
        _lstProfiles.SelectedIndexChanged += (_, _) => UpdateDetail();
        _lstProfiles.DoubleClick += (_, _) => EditSelected();

        // 左侧按钮两行，改用 FlowLayoutPanel 摆脱手工算坐标
        var sideButtonsTop = new FlowLayoutPanel
        {
            Location = new Point(0, 432),
            Size = new Size(266, 36),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Theme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        ConfigureSmallButton(_btnNew, "新建", 62, UiRole.Primary);
        ConfigureSmallButton(_btnCopy, "复制", 62, UiRole.Secondary);
        ConfigureSmallButton(_btnEdit, "编辑", 62, UiRole.Secondary);
        ConfigureSmallButton(_btnDelete, "删除", 58, UiRole.Danger);
        _btnNew.Click += (_, _) => CreateProfile();
        _btnCopy.Click += (_, _) => CopyProfile();
        _btnEdit.Click += (_, _) => EditSelected();
        _btnDelete.Click += (_, _) => DeleteProfile();
        sideButtonsTop.Controls.AddRange(new Control[] { _btnNew, _btnCopy, _btnEdit, _btnDelete });

        var sideButtonsBottom = new FlowLayoutPanel
        {
            Location = new Point(0, 470),
            Size = new Size(266, 36),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Theme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        ConfigureSmallButton(_btnUp, "上移", 62, UiRole.Secondary);
        ConfigureSmallButton(_btnDown, "下移", 62, UiRole.Secondary);
        ConfigureSmallButton(_btnImport, "导入配置", 126, UiRole.Secondary);
        _btnUp.Click += (_, _) => MoveSelected(-1);
        _btnDown.Click += (_, _) => MoveSelected(1);
        _btnImport.Click += (_, _) => ImportConfig();
        sideButtonsBottom.Controls.AddRange(new Control[] { _btnUp, _btnDown, _btnImport });

        _chkIncludeVirtual.Text = "包含虚拟网卡";
        _chkIncludeVirtual.Location = new Point(2, 506);
        _chkIncludeVirtual.Size = new Size(258, 22);
        _chkIncludeVirtual.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _chkIncludeVirtual.ForeColor = Theme.TextMuted;
        // 勾选 = 包含 = 不过滤，与服务的 ExcludeVirtualAdapters 语义相反
        _chkIncludeVirtual.Checked = !_services.Adapters.ExcludeVirtualAdapters;
        _chkIncludeVirtual.CheckedChanged += OnIncludeVirtualChanged;

        _side.Controls.AddRange(new Control[]
        {
            _lblSide, _lstProfiles, sideButtonsTop, sideButtonsBottom, _chkIncludeVirtual,
        });

        Controls.Add(_side);
    }

    private static void ConfigureSmallButton(Button button, string text, int width, UiRole role)
    {
        button.Text = text;
        button.Size = new Size(width, 32);
        button.Tag = role;
        button.Margin = new Padding(0, 0, 6, 0);
    }

    private void BuildRight()
    {
        _right.Location = new Point(292, 82);
        _right.Size = new Size(870, 630);
        _right.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _right.BackColor = Theme.Bg;

        // 状态卡片
        _grpStatus.Text = "当前网络状态";
        _grpStatus.Location = new Point(0, 0);
        _grpStatus.Size = new Size(866, 226);
        _grpStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _txtStatus.Location = new Point(14, 30);
        _txtStatus.Size = new Size(838, 184);
        _txtStatus.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtStatus.Multiline = true;
        _txtStatus.ReadOnly = true;
        _txtStatus.ScrollBars = ScrollBars.Vertical;
        _txtStatus.BorderStyle = BorderStyle.None;
        _txtStatus.BackColor = Theme.LogBg;
        _txtStatus.Font = Theme.MonoFont(9f);
        _grpStatus.Controls.Add(_txtStatus);

        // 详情卡片
        _grpDetail.Text = "配置集内容";
        _grpDetail.Location = new Point(0, 234);
        _grpDetail.Size = new Size(866, 212);
        _grpDetail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _txtDetail.Location = new Point(14, 30);
        _txtDetail.Size = new Size(838, 170);
        _txtDetail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtDetail.Multiline = true;
        _txtDetail.ReadOnly = true;
        _txtDetail.ScrollBars = ScrollBars.Vertical;
        _txtDetail.BorderStyle = BorderStyle.None;
        _txtDetail.BackColor = Theme.LogBg;
        _txtDetail.Font = Theme.MonoFont(9f);
        _grpDetail.Controls.Add(_txtDetail);

        // 操作区：8 个按钮，改用 FlowLayoutPanel
        _action.Location = new Point(0, 454);
        _action.Size = new Size(866, 46);
        _action.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _action.BackColor = Theme.Bg;

        var actions = new FlowLayoutPanel
        {
            Location = new Point(0, 5),
            Size = new Size(866, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Theme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        ConfigureActionButton(_btnApply, "应用此配置集", 128, UiRole.Primary);
        ConfigureActionButton(_btnPreview, "预览此配置集", 116, UiRole.Secondary);
        ConfigureActionButton(_btnShortcut, "生成桌面快捷方式", 136, UiRole.Secondary);
        ConfigureActionButton(_btnRefresh, "刷新状态", 88, UiRole.Secondary);
        ConfigureActionButton(_btnBackup, "备份记录", 92, UiRole.Secondary);
        ConfigureActionButton(_btnExport, "导出配置", 88, UiRole.Secondary);
        ConfigureActionButton(_btnOpenFile, "打开配置文件", 104, UiRole.Secondary);
        ConfigureActionButton(_btnCopyLog, "复制日志", 72, UiRole.Secondary);

        _btnApply.Click += (_, _) => ApplySelected();
        _btnPreview.Click += (_, _) => PreviewSelected();
        _btnShortcut.Click += (_, _) => CreateShortcut();
        _btnRefresh.Click += (_, _) => RefreshAll();
        _btnBackup.Click += (_, _) => ShowBackupManager();
        _btnExport.Click += (_, _) => ExportConfig();
        _btnOpenFile.Click += (_, _) => OpenConfigFile();
        _btnCopyLog.Click += (_, _) => CopyLog();

        actions.Controls.AddRange(new Control[]
        {
            _btnApply, _btnPreview, _btnShortcut, _btnRefresh,
            _btnBackup, _btnExport, _btnOpenFile, _btnCopyLog,
        });
        _action.Controls.Add(actions);

        // 日志卡片
        _grpLog.Text = "操作日志";
        _grpLog.Location = new Point(0, 508);
        _grpLog.Size = new Size(866, 118);
        _grpLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _txtLog.Location = new Point(14, 30);
        _txtLog.Size = new Size(838, 76);
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.BorderStyle = BorderStyle.None;
        _txtLog.BackColor = Theme.LogBg;
        _txtLog.ForeColor = Theme.Text;
        _txtLog.Font = Theme.MonoFont(9f);
        _grpLog.Controls.Add(_txtLog);

        _right.Controls.AddRange(new Control[] { _grpStatus, _grpDetail, _action, _grpLog });
        Controls.Add(_right);
    }

    private static void ConfigureActionButton(Button button, string text, int width, UiRole role)
    {
        button.Text = text;
        button.Size = new Size(width, 36);
        button.Tag = role;
        button.Margin = new Padding(0, 0, 6, 0);
    }

    // ── 列表自绘（两行：名称 + 备注）────────────────────────────────────────

    private void OnDrawProfileItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _lstProfiles.Items.Count) return;

        var item = _lstProfiles.Items[e.Index];
        if (item is not Profile profile) return;

        PaintStats.CountListRedraw();

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;

        // 整行底色（用缓存画刷，避免每次重绘都 new/dispose GDI 对象）
        g.FillRectangle(_listBgBrush, bounds);

        // 两行文字：所有偏移按「行高相对设计值 44」的比例换算。
        // 不能写死像素值 —— 高 DPI 下行高已被放大到 66，硬编码的 6px/23px
        // 会把两行字压在顶部、下半截被裁掉（这是实际踩到的 bug）。
        const int designItemHeight = 44;
        var k = _lstProfiles.ItemHeight / (double)designItemHeight;

        var padX = (int)Math.Round(14 * k);
        var padRight = (int)Math.Round(24 * k);
        var mainTop = (int)Math.Round(6 * k);
        var mainHeight = (int)Math.Round(18 * k);
        var subGap = (int)Math.Round(23 * k);
        var subHeight = (int)Math.Round(16 * k);

        // 选中块与竖条的几何也按同一比例换算
        if (selected)
        {
            var inset = (int)Math.Round(3 * k);
            var blockRect = new Rectangle(
                bounds.X + inset, bounds.Y + inset,
                bounds.Width - inset * 3, bounds.Height - inset * 2);
            if (blockRect.Width > 0 && blockRect.Height > 0)
            {
                using var path = Theme.CreateRoundPath(blockRect, (int)Math.Round(4 * k));
                g.FillPath(_listSelBlockBrush, path);
            }

            var barRect = new Rectangle(
                bounds.X + inset,
                bounds.Y + (int)Math.Round(9 * k),
                Math.Max(2, (int)Math.Round(3 * k)),
                bounds.Height - (int)Math.Round(18 * k));
            if (barRect.Height > 0)
            {
                using var path = Theme.CreateRoundPath(barRect, Math.Max(1, (int)Math.Round(1 * k)));
                g.FillPath(_listSelBarBrush, path);
            }
        }

        var textLeft = bounds.X + padX;
        var textWidth = bounds.Width - padX - padRight;
        if (textWidth <= 0) return;

        // 字体与画刷用缓存实例，绝不在 DrawItem 里 new。
        // 原因：点一下别处 → 列表失焦/重绘 → DrawItem 对每个可见项各跑一次；
        // 若每次都 new Font/SolidBrush，会反复创建销毁 GDI 句柄，
        // 既慢又让整块区域出现可见的闪烁。这是本次「点别处闪一下」的直接原因之一。
        var mainFont = selected ? _listFontBold : _listFontNormal;
        var subFont = _listSubFont;
        var mainColor = selected ? Theme.Accent : Theme.Text;
        var subColor = selected ? Theme.ListSubSelected : Theme.TextMuted;

        var mainRect = new RectangleF(textLeft, bounds.Y + mainTop, textWidth, mainHeight);
        TextRenderer.DrawText(
            g, profile.Name, mainFont, Rectangle.Round(mainRect), mainColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var subText = string.IsNullOrWhiteSpace(profile.Remark) ? SummarizeAdapters(profile) : profile.Remark;
        var subRect = new RectangleF(textLeft, bounds.Y + subGap, textWidth, subHeight);
        TextRenderer.DrawText(
            g, subText, subFont, Rectangle.Round(subRect), subColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>备注为空时，副行改显示「模式 + 网卡数」的摘要。</summary>
    private string SummarizeAdapters(Profile profile)
    {
        if (profile.Adapters.Count == 0) return "（空）";

        var first = profile.Adapters[0];
        var mode = first.Mode == "static" ? "静态" : "DHCP";
        var count = profile.Adapters.Count;
        return count == 1 ? mode : $"{mode} · {count} 项";
    }

    // ── 数据刷新 ──────────────────────────────────────────────────────────

    private void OnShown(object? sender, EventArgs e)
    {
        Activate();
        RefreshAll();
    }

    /// <summary>刷新配置集列表、状态、详情与权限标签。</summary>
    private void RefreshAll()
    {
        RefreshProfileList();
        RefreshStatus();
        UpdateAdminLabel();
        UpdateConfigLabel();
    }

    private void RefreshProfileList()
    {
        var profiles = _services.Profiles.Data.Profiles;
        var selectedName = CurrentProfile()?.Name;

        // 内容没变就什么都不做 —— 这是消除闪烁的关键一步。
        // 旧实现每次刷新都 Items.Clear() + 逐条 Add，清空与重填之间列表是空的，
        // 任何触发了刷新的动作（点别处、切焦点）都会看到「闪一下」。
        var unchanged = _lstProfiles.Items.Count == profiles.Count;
        if (unchanged)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (!ReferenceEquals(_lstProfiles.Items[i], profiles[i]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            UpdateDetail();
            return;
        }

        _lstProfiles.BeginUpdate();
        try
        {
            _lstProfiles.Items.Clear();
            foreach (var profile in profiles)
            {
                _lstProfiles.Items.Add(profile);
            }

            // 尽量保持原选中项
            var index = 0;
            if (selectedName is not null)
            {
                for (var i = 0; i < _lstProfiles.Items.Count; i++)
                {
                    if (_lstProfiles.Items[i] is Profile p && p.Name == selectedName)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (_lstProfiles.Items.Count > 0)
            {
                _lstProfiles.SelectedIndex = index;
            }
        }
        finally
        {
            _lstProfiles.EndUpdate();
        }

        UpdateDetail();
    }

    private void RefreshStatus()
    {
        try
        {
            _txtStatus.Text = _services.Status.GetStatusText(includeIpv6: true);
        }
        catch (Exception ex)
        {
            _txtStatus.Text = $"读取状态失败：{ex.Message}";
        }
    }

    private void UpdateAdminLabel()
    {
        var isAdmin = Elevation.IsAdmin();
        _lblAdmin.Text = isAdmin ? "已获取管理员权限" : "普通权限（切换需管理员）";
        _lblAdmin.ForeColor = isAdmin ? Theme.AdminGranted : Theme.AdminNormal;
        _btnElevate.Visible = !isAdmin;
    }

    private void UpdateConfigLabel()
    {
        _lblConfig.Text = $"配置文件：{_services.Config.ConfigPath}";
    }

    private void UpdateDetail()
    {
        var profile = CurrentProfile();
        if (profile is null || profile.Adapters.Count == 0)
        {
            _txtDetail.Text = "请选择左侧的配置集查看内容。";
            return;
        }

        var lines = new List<string>
        {
            $"配置集：{profile.Name}",
        };

        if (!string.IsNullOrWhiteSpace(profile.Remark))
        {
            lines.Add($"备注：{profile.Remark}");
        }

        lines.Add($"共 {profile.Adapters.Count} 项网卡配置：");
        lines.Add("");

        var i = 1;
        foreach (var adapter in profile.Adapters)
        {
            var display = _services.Adapters.GetDisplayText(adapter.Adapter);
            var mode = adapter.Mode == "static" ? "静态" : "DHCP";

            lines.Add($"{i}. 网卡匹配：{display}   [{(adapter.Enabled ? "启用" : "禁用")}]   模式：{mode}");

            if (adapter.Mode == "static")
            {
                var mask = string.IsNullOrWhiteSpace(adapter.Mask) ? "255.255.255.0" : adapter.Mask;
                lines.Add($"   IP：{adapter.Ip}    掩码：{mask}");

                if (adapter.Gateways is { Count: > 0 })
                {
                    lines.Add($"   网关：{string.Join(", ", adapter.Gateways)}");
                }

                if (adapter.DefaultMetric is not null)
                {
                    lines.Add($"   默认跃点：{adapter.DefaultMetric}");
                }
            }

            if (adapter.Dns is { Count: > 0 })
            {
                lines.Add($"   DNS：{string.Join(", ", adapter.Dns)}");
            }
            else if (adapter.DhcpDns)
            {
                lines.Add("   DNS：自动");
            }

            if (adapter.Routes is { Count: > 0 })
            {
                foreach (var route in adapter.Routes)
                {
                    lines.Add($"   附加路由：{route}");
                }
            }

            lines.Add("");
            i++;
        }

        _txtDetail.Text = string.Join("\r\n", lines);
    }

    private Profile? CurrentProfile()
        => _lstProfiles.SelectedItem as Profile;

    // ── 事件处理 ──────────────────────────────────────────────────────────

    private void OnElevateClick(object? sender, EventArgs e)
    {
        if (Elevation.RestartElevated(_services.Options))
        {
            Close();
        }
    }

    private void OnIncludeVirtualChanged(object? sender, EventArgs e)
    {
        _services.Adapters.ExcludeVirtualAdapters = !_chkIncludeVirtual.Checked;
        RefreshStatus();
    }

    private void CreateProfile()
    {
        var dialogs = new DialogService(_services);
        if (dialogs.ShowProfileEditor(null))
        {
            RefreshAll();
        }
    }

    private void CopyProfile()
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        try
        {
            var copy = _services.Profiles.Duplicate(profile.Name);
            _services.Profiles.Save();
            RefreshProfileList();

            // 选中新复制出来的那条
            for (var i = 0; i < _lstProfiles.Items.Count; i++)
            {
                if (_lstProfiles.Items[i] is Profile p && p.Name == copy.Name)
                {
                    _lstProfiles.SelectedIndex = i;
                    break;
                }
            }

            Log.Ok($"已复制配置集「{profile.Name}」为「{copy.Name}」");
        }
        catch (Exception ex)
        {
            Log.Error($"复制配置集失败：{ex.Message}");
            MessageBox.Show(this, $"复制配置集失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EditSelected()
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        var dialogs = new DialogService(_services);
        if (dialogs.ShowProfileEditor(profile))
        {
            RefreshAll();
        }
    }

    private void DeleteProfile()
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        var answer = MessageBox.Show(
            this,
            $"确定要删除配置集「{profile.Name}」吗？\r\n\r\n此操作不可撤销（配置文件会自动备份一份）。",
            "NetSwitch - 删除配置集",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        try
        {
            if (_services.Profiles.Remove(profile.Name))
            {
                _services.Profiles.Save();
                RefreshAll();
                Log.Ok($"已删除配置集「{profile.Name}」");
            }
            else
            {
                Log.Warn($"未找到要删除的配置集「{profile.Name}」");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"删除配置集失败：{ex.Message}");
            MessageBox.Show(this, $"删除配置集失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MoveSelected(int delta)
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        var from = _lstProfiles.SelectedIndex;
        if (_services.Profiles.Move(profile.Name, delta))
        {
            _services.Profiles.Save();
            RefreshProfileList();

            var to = from + delta;
            if (to >= 0 && to < _lstProfiles.Items.Count)
            {
                _lstProfiles.SelectedIndex = to;
            }
        }
    }

    private void ImportConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要导入的配置文件",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (_services.Transfer.ImportFile(dialog.FileName, _services.Config))
            {
                RefreshAll();
                MessageBox.Show(this, $"配置已导入：\r\n{dialog.FileName}", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "导入失败，请查看日志了解原因。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"导入配置失败：{ex.Message}");
            MessageBox.Show(this, $"导入配置失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportConfig()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出配置",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"NetSwitch-profiles-{DateTime.Now:yyyyMMdd}.json",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (_services.Transfer.ExportFile(dialog.FileName, _services.Config.Data))
            {
                MessageBox.Show(this, $"配置已导出到：\r\n{dialog.FileName}", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "导出失败，请查看日志了解原因。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"导出配置失败：{ex.Message}");
            MessageBox.Show(this, $"导出配置失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplySelected()
    {
        var profile = CurrentProfile();
        if (profile is null)
        {
            MessageBox.Show(this, "请先选择一个配置集。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!Elevation.IsAdmin())
        {
            var answer = MessageBox.Show(
                this,
                "修改网络配置需要管理员权限。\r\n\r\n是否以管理员身份重启本程序？",
                "NetSwitch - 需要管理员权限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer == DialogResult.Yes && Elevation.RestartElevated(_services.Options))
            {
                Close();
            }
            return;
        }

        // 应用前把影响范围列给用户看（旧版行为：让用户确认会动到哪些网卡）
        var affected = new List<string>();
        foreach (var adapter in profile.Adapters)
        {
            if (!adapter.Enabled) continue;
            var matched = _services.Adapters.FindMatched(adapter.Adapter);
            foreach (var item in matched)
            {
                if (!affected.Contains(item.Name)) affected.Add(item.Name);
            }
        }

        if (affected.Count == 0)
        {
            MessageBox.Show(this,
                "该配置集没有匹配到任何本机网卡，未做任何改动。\r\n\r\n请检查配置集里的「网卡匹配」字段。",
                "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"即将应用配置集「{profile.Name}」，将改动以下 {affected.Count} 块网卡：\r\n\r\n" +
            string.Join("\r\n", affected.Select(n => "  • " + n)) +
            "\r\n\r\n切换前会自动备份当前网络状态。是否继续？",
            "NetSwitch - 确认切换",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var ok = _services.Engine.Apply(profile.Name);
            Cursor = Cursors.Default;

            RefreshStatus();

            if (ok)
            {
                MessageBox.Show(this, $"配置集「{profile.Name}」已生效。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    "切换过程中有部分网卡失败，请查看下方操作日志了解详情。",
                    "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            Log.Error($"应用配置集失败：{ex.Message}");
            MessageBox.Show(this, $"应用配置集失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PreviewSelected()
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        try
        {
            _services.Engine.Apply(profile.Name, dryRun: true);

            var lines = new List<string> { $"配置集「{profile.Name}」将影响以下网卡：", "" };

            foreach (var adapter in profile.Adapters)
            {
                if (!adapter.Enabled)
                {
                    lines.Add($"  {adapter.Adapter}  （已禁用，跳过）");
                    continue;
                }

                var matched = _services.Adapters.FindMatched(adapter.Adapter);
                var target = matched.Count == 0
                    ? "未匹配到网卡"
                    : string.Join("、", matched.Select(a => $"{a.Name}(#{a.InterfaceIndex})"));

                var mode = adapter.Mode == "static" ? $"静态 {adapter.Ip}" : "DHCP";
                lines.Add($"  {adapter.Adapter}  →  {target}    [{mode}]");
            }

            MessageBox.Show(this, string.Join("\r\n", lines), "NetSwitch - 预览",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error($"预览失败：{ex.Message}");
        }
    }

    private void CreateShortcut()
    {
        var profile = CurrentProfile();
        if (profile is null) return;

        try
        {
            if (_services.Shortcuts.CreateProfileShortcut(profile.Name))
            {
                MessageBox.Show(this,
                    $"已在桌面创建快捷方式「{UiContract.Version} - {profile.Name}」。\r\n\r\n" +
                    "双击该快捷方式即可直接切换到本配置集。",
                    "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "创建快捷方式失败，请查看日志了解原因。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"创建快捷方式失败：{ex.Message}");
        }
    }

    private void ShowBackupManager()
    {
        var dialogs = new DialogService(_services);
        dialogs.ShowBackupManager();
        RefreshAll();
    }

    private void OpenConfigFile()
    {
        try
        {
            var path = _services.Config.ConfigPath;
            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"配置文件不存在：\r\n{path}", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"打开配置文件失败：{ex.Message}");
        }
    }

    private void CopyLog()
    {
        try
        {
            var tail = Log.GetTail(300);
            if (string.IsNullOrWhiteSpace(tail))
            {
                MessageBox.Show(this, "当前没有可复制的日志。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Clipboard.SetText(tail);
            Log.Ok("最近 300 行日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Log.Error($"复制日志失败：{ex.Message}");
        }
    }

    /// <summary>把日志接进界面日志框。由 Program 在窗体创建后调用。</summary>
    public void BindLogBox()
    {
        Log.UiSink = (line, level) =>
        {
            if (_txtLog.IsDisposed) return;

            var color = level switch
            {
                LogLevel.Ok => Color.SeaGreen,
                LogLevel.Warn => Color.DarkOrange,
                LogLevel.Error => Color.Firebrick,
                _ => Color.DimGray,
            };

            // 频繁追加时用 SelectionColor 性能较差，旧版注释已记录，这里沿用简单追加
            var start = _txtLog.TextLength;
            _txtLog.AppendText(line + "\r\n");

            try
            {
                _txtLog.Select(start, line.Length);
                _txtLog.SelectionColor = color;
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
            catch
            {
                // 上色失败不影响文本已追加
            }
        };
    }

    /// <summary>释放列表自绘用的缓存资源，并解绑日志接收器。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 避免窗体销毁后日志仍往已释放的控件里写
            if (ReferenceEquals(Log.UiSink, Log.UiSink))
            {
                Log.UiSink = null;
            }

            _listFontNormal.Dispose();
            _listFontBold.Dispose();
            _listSubFont.Dispose();
            _listBgBrush.Dispose();
            _listSelBlockBrush.Dispose();
            _listSelBarBrush.Dispose();
        }

        base.Dispose(disposing);
    }
}

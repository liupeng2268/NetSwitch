using System.Drawing;
using System.Windows.Forms;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 配置集编辑器。对应旧版 <c>Show-ProfileEditor</c>（第 2662-2998 行）。
///
/// 两处从旧版踩坑记录里继承的关键做法：
///   1. <b>显式定义 DataGridView 的列</b>，绝不用自动生成列 ——
///      旧版曾因「自动生成列」导致整个窗口打不开。
///   2. <b>打开时预置一行空行</b> —— 否则用户不知道该在哪里填 IP。
/// </summary>
public sealed class ProfileEditorForm : Form
{
    private readonly AppServices _services;
    private readonly Profile _working;
    private readonly bool _isNew;

    private readonly TextBox _txtName = new();
    private readonly TextBox _txtRemark = new();
    private readonly DataGridView _grid = new();
    private readonly ComboBox _cboAdapter = new();
    private readonly Button _btnAddRow = new();
    private readonly Button _btnAddWildcard = new();
    private readonly Button _btnRemoveRow = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnCancel = new();

    private const string ColEnabled = "colEnabled";
    private const string ColAdapter = "colAdapter";
    private const string ColMode = "colMode";
    private const string ColIp = "colIp";
    private const string ColMask = "colMask";
    private const string ColGateway = "colGateway";
    private const string ColDns = "colDns";
    private const string ColMetric = "colMetric";
    private const string ColRoutes = "colRoutes";
    private const string ColDhcpCoexist = "colDhcpCoexist";
    private const string ColIpv6 = "colIpv6";

    public ProfileEditorForm(AppServices services, Profile? source)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _isNew = source is null;

        // 深拷贝，避免取消时污染原数据
        _working = source is null
            ? new Profile { Name = "", Remark = "", Adapters = new List<AdapterConfig>() }
            : CloneProfile(source);

        Text = _isNew ? "新建配置集" : $"编辑配置集 - {source!.Name}";
        Size = new Size(1020, 620);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        BackColor = Theme.Bg;
        Font = Theme.UiFont(9f);

        BuildLayout();
        LoadData();

        UiStyler.Apply(this);
        UiStyler.ScaleForDpi(this);

        Shown += OnShown;
    }

    private static Profile CloneProfile(Profile source)
    {
        return new Profile
        {
            Name = source.Name,
            Remark = source.Remark,
            Adapters = source.Adapters.Select(a => new AdapterConfig
            {
                Adapter = a.Adapter,
                Enabled = a.Enabled,
                Mode = a.Mode,
                Ip = a.Ip,
                Mask = a.Mask,
                DhcpDns = a.DhcpDns,
                DefaultMetric = a.DefaultMetric,
                Gateways = a.Gateways is null ? null : new List<string>(a.Gateways),
                Dns = a.Dns is null ? null : new List<string>(a.Dns),
                Routes = a.Routes is null ? null : new List<string>(a.Routes),
                DhcpStaticCoexist = a.DhcpStaticCoexist,
                Ipv6Enabled = a.Ipv6Enabled,
            }).ToList(),
        };
    }

    private void BuildLayout()
    {
        var lblName = new Label
        {
            Text = "配置集名称",
            Location = new Point(18, 16),
            Size = new Size(76, 22),
            ForeColor = Theme.Text,
        };

        _txtName.Location = new Point(100, 14);
        _txtName.Size = new Size(280, 26);
        _txtName.BorderStyle = BorderStyle.FixedSingle;

        var lblRemark = new Label
        {
            Text = "备注",
            Location = new Point(396, 16),
            Size = new Size(40, 22),
            ForeColor = Theme.Text,
        };

        _txtRemark.Location = new Point(440, 14);
        _txtRemark.Size = new Size(548, 26);
        _txtRemark.BorderStyle = BorderStyle.FixedSingle;
        _txtRemark.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // 网卡下拉 + 添加按钮行
        var lblAdapter = new Label
        {
            Text = "从本机网卡添加",
            Location = new Point(18, 54),
            Size = new Size(100, 22),
            ForeColor = Theme.TextMuted,
        };

        _cboAdapter.Location = new Point(124, 52);
        _cboAdapter.Size = new Size(300, 26);
        _cboAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboAdapter.Font = Theme.UiFont(9f);

        _btnAddRow.Text = "添加";
        _btnAddRow.Location = new Point(432, 51);
        _btnAddRow.Size = new Size(72, 28);
        _btnAddRow.Tag = UiRole.Primary;
        _btnAddRow.Click += (_, _) => AddRowFromCombo();

        _btnAddWildcard.Text = "加通配 * 行";
        _btnAddWildcard.Location = new Point(510, 51);
        _btnAddWildcard.Size = new Size(110, 28);
        _btnAddWildcard.Tag = UiRole.Secondary;
        _btnAddWildcard.Click += (_, _) => AddRow(new AdapterConfig
        {
            Adapter = "*", Enabled = true, Mode = "dhcp",
        });

        _btnRemoveRow.Text = "删除选中行";
        _btnRemoveRow.Location = new Point(626, 51);
        _btnRemoveRow.Size = new Size(102, 28);
        _btnRemoveRow.Tag = UiRole.Danger;
        _btnRemoveRow.Click += (_, _) => RemoveSelectedRow();

        // 表格：显式定义列（旧版踩坑点）
        _grid.Location = new Point(18, 88);
        _grid.Size = new Size(970, 440);
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.BackgroundColor = Theme.Card;

        BuildColumns();

        // 底部按钮
        _btnSave.Text = "保存";
        _btnSave.Location = new Point(796, 540);
        _btnSave.Size = new Size(92, 34);
        _btnSave.Tag = UiRole.Primary;
        _btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _btnSave.Click += OnSaveClick;

        _btnCancel.Text = "取消";
        _btnCancel.Location = new Point(896, 540);
        _btnCancel.Size = new Size(92, 34);
        _btnCancel.Tag = UiRole.Secondary;
        _btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _btnCancel.DialogResult = DialogResult.Cancel;

        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[]
        {
            lblName, _txtName, lblRemark, _txtRemark,
            lblAdapter, _cboAdapter, _btnAddRow, _btnAddWildcard, _btnRemoveRow,
            _grid, _btnSave, _btnCancel,
        });
    }

    /// <summary>
    /// 显式定义列。旧版曾用自动生成列，导致窗口打不开 —— 这里绝不依赖自动生成。
    /// </summary>
    private void BuildColumns()
    {
        DataGridViewTextBoxColumn Text(string name, string header, int width)
            => new()
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };

        // 11 列基准宽度合计 958，与表格控件宽 970 对齐，避免横向溢出。
        // UiStyler.ScaleForDpi 会把表格尺寸与列宽按同一系数放大，故只改基准列宽即可；
        // 窗体 / 控件尺寸与字体一律不动（DPI 铁律）。
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColEnabled,
            HeaderText = "启用",
            Width = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(Text(ColAdapter, "网卡匹配（名称/描述/MAC）", 150));
        _grid.Columns.Add(Text(ColMode, "模式", 54));
        _grid.Columns.Add(Text(ColIp, "IP 地址", 96));
        _grid.Columns.Add(Text(ColMask, "子网掩码", 96));
        _grid.Columns.Add(Text(ColGateway, "网关（可写 1.1.1.1:10）", 112));
        _grid.Columns.Add(Text(ColDns, "DNS（逗号分隔）", 112));
        _grid.Columns.Add(Text(ColMetric, "默认跃点", 58));
        _grid.Columns.Add(Text(ColRoutes, "附加路由（10.0.0.0/8>1.1.1.1）", 124));
        // 两个可选开关用普通 TextBox 列表达三态（空/1/0），避免 CheckBox 只能两态、
        // 也避免 ComboBox 触发 DataGridView DataError。列头注明取值含义。
        _grid.Columns.Add(Text(ColDhcpCoexist, "静态共存 空/1/0", 72));
        _grid.Columns.Add(Text(ColIpv6, "IPv6 空/1/0", 44));
    }

    private void LoadData()
    {
        _txtName.Text = _working.Name;
        _txtRemark.Text = _working.Remark;

        // 填充网卡下拉
        _cboAdapter.Items.Clear();
        foreach (var adapter in _services.Adapters.GetAdapters())
        {
            _cboAdapter.Items.Add(adapter);
        }
        _cboAdapter.DisplayMember = nameof(AdapterInfo.Name);
        if (_cboAdapter.Items.Count > 0) _cboAdapter.SelectedIndex = 0;

        foreach (var adapter in _working.Adapters)
        {
            AddRow(adapter);
        }
    }

    private void OnShown(object? sender, EventArgs e)
    {
        // 预置一行空行（旧版踩坑点：否则用户无处填 IP）
        if (_grid.Rows.Count == 0)
        {
            AddRow(new AdapterConfig
            {
                Adapter = "", Enabled = true, Mode = "static", Mask = "255.255.255.0",
            });
        }

        // 定位到 IP 单元格，用户可直接开始输入
        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = _grid.Rows[0].Cells[ColIp];
            _grid.BeginEdit(true);
        }
    }

    private void AddRowFromCombo()
    {
        if (_cboAdapter.SelectedItem is not AdapterInfo adapter)
        {
            MessageBox.Show(this, "请先选择一块网卡。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 存 MAC 而非名称：以后在系统里改名也不会失配
        var storage = _services.Adapters.ToStorageValue(adapter.Name);
        AddRow(new AdapterConfig
        {
            Adapter = string.IsNullOrWhiteSpace(storage) ? adapter.Name : storage,
            Enabled = true,
            Mode = "static",
            Mask = "255.255.255.0",
        });
    }

    private void AddRow(AdapterConfig config)
    {
        var index = _grid.Rows.Add();
        var row = _grid.Rows[index];

        row.Cells[ColEnabled].Value = config.Enabled;
        row.Cells[ColAdapter].Value = _services.Adapters.GetDisplayText(config.Adapter);
        row.Cells[ColMode].Value = config.Mode == "static" ? "static" : "dhcp";
        row.Cells[ColIp].Value = config.Ip;
        row.Cells[ColMask].Value = config.Mask;
        row.Cells[ColGateway].Value = config.Gateways is null ? "" : string.Join(", ", config.Gateways);
        row.Cells[ColDns].Value = config.Dns is null ? "" : string.Join(", ", config.Dns);
        row.Cells[ColMetric].Value = config.DefaultMetric?.ToString() ?? "";
        row.Cells[ColRoutes].Value = config.Routes is null ? "" : string.Join(", ", config.Routes);
        // 模型 -> 行（三态）：null -> '' / true -> '1' / false -> '0'
        row.Cells[ColDhcpCoexist].Value = config.DhcpStaticCoexist switch { true => "1", false => "0", _ => "" };
        row.Cells[ColIpv6].Value = config.Ipv6Enabled switch { true => "1", false => "0", _ => "" };

        // 把原始匹配值藏在行的 Tag 里，保存时优先用它（显示的是名称，存的是 MAC）
        row.Tag = config.Adapter;

        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = row.Cells[ColAdapter];
        }
    }

    private void RemoveSelectedRow()
    {
        if (_grid.CurrentRow is null) return;
        if (_grid.Rows.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一行。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _grid.Rows.Remove(_grid.CurrentRow);
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var name = _txtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请填写配置集名称。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        // 新建时检查重名
        if (_isNew && _services.Profiles.FindByName(name) is not null)
        {
            MessageBox.Show(this, $"已存在同名配置集「{name}」，请换一个名称。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        var adapters = new List<AdapterConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            var raw = row.Tag as string ?? "";
            var display = row.Cells[ColAdapter].Value?.ToString()?.Trim() ?? "";

            // 用户可能改了网卡匹配：优先按显示的文本重新转存储值
            var storage = !string.IsNullOrWhiteSpace(display) && display != _services.Adapters.GetDisplayText(raw)
                ? _services.Adapters.ToStorageValue(display)
                : raw;

            if (string.IsNullOrWhiteSpace(storage) && string.IsNullOrWhiteSpace(display)) continue;

            var modeText = row.Cells[ColMode].Value?.ToString()?.Trim().ToLowerInvariant() ?? "dhcp";
            var mode = modeText is "static" or "dhcp" ? modeText : "dhcp";

            var ip = row.Cells[ColIp].Value?.ToString()?.Trim() ?? "";
            var mask = row.Cells[ColMask].Value?.ToString()?.Trim() ?? "";
            var gateways = ParseList(row.Cells[ColGateway].Value?.ToString());
            var dns = ParseList(row.Cells[ColDns].Value?.ToString());
            var routes = ParseList(row.Cells[ColRoutes].Value?.ToString());
            var metricText = row.Cells[ColMetric].Value?.ToString()?.Trim() ?? "";

            // 两个新增开关（行 -> 模型，三态）：'' 未设置写 null（不产生新键）、
            // '1' 启用写 true、'0' 禁用写 false。未设置时绝不写 false，
            // 保住旧 profiles.json 不出现新键的兼容性。
            var dhcpStaticCoexist = ParseTriState(row.Cells[ColDhcpCoexist].Value);
            var ipv6Enabled = ParseTriState(row.Cells[ColIpv6].Value);

            adapters.Add(new AdapterConfig
            {
                Adapter = string.IsNullOrWhiteSpace(storage) ? display : storage,
                Enabled = row.Cells[ColEnabled].Value is true,
                Mode = mode,
                Ip = ip,
                Mask = mask,
                DhcpDns = dns.Count == 0,
                DefaultMetric = Config.ConfigRepair.NormalizeMetric(metricText),
                Gateways = gateways,
                Dns = dns,
                Routes = routes,
                DhcpStaticCoexist = dhcpStaticCoexist,
                Ipv6Enabled = ipv6Enabled,
            });
        }

        if (adapters.Count == 0)
        {
            MessageBox.Show(this, "至少要配置一项网卡。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var renamedFrom = _isNew ? null : _working.Name;

        _working.Name = name;
        _working.Remark = _txtRemark.Text.Trim();
        _working.Adapters = adapters;

        try
        {
            if (_isNew)
            {
                _services.Profiles.Add(_working);
            }
            else
            {
                // 编辑：替换原对象
                var target = _services.Profiles.FindByName(renamedFrom!);
                if (target is not null)
                {
                    target.Name = _working.Name;
                    target.Remark = _working.Remark;
                    target.Adapters = _working.Adapters;
                }
            }

            _services.Profiles.Save();

            // 改名后同步桌面快捷方式（旧版行为）
            if (renamedFrom is not null && renamedFrom != name)
            {
                _services.Shortcuts.SyncOnRename(renamedFrom, name);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置集失败：{ex.Message}");
            MessageBox.Show(this, $"保存失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>三态单元格文本 -> 可空 bool：'1' 启用 / '0' 禁用 / 其他（含 ''）未设置。</summary>
    private static bool? ParseTriState(object? value)
        => value?.ToString()?.Trim() switch { "1" => true, "0" => false, _ => null };

    private static List<string> ParseList(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var part in text.Split(new[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result;
    }
}

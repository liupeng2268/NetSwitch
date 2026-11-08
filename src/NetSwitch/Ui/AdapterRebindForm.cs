using System.Drawing;
using System.Windows.Forms;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 网卡重绑对话框。对应旧版 <c>Show-AdapterRebindDialog</c>（第 2509-2661 行）。
///
/// 场景：网卡在 Windows 里被改名后，配置集里的匹配值（名称）失效。
/// 此对话框让用户从当前可用网卡里挑一块，把新的匹配值写回配置集。
/// </summary>
public sealed class AdapterRebindForm : Form
{
    private readonly ListBox _lstAdapters = new();
    private readonly Label _lblHint = new();
    private readonly Button _btnBind = new();
    private readonly Button _btnCancel = new();

    /// <summary>用户选中的网卡名称；取消时为 null。</summary>
    public string? SelectedAdapter { get; private set; }

    public AdapterRebindForm(string pattern, List<AdapterInfo> available)
    {
        Text = "重新绑定网卡";
        Size = new Size(520, 420);
        MinimumSize = new Size(480, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Bg;
        Font = Theme.UiFont(9f);

        _lblHint.Text = $"配置里的「{pattern}」没有匹配到任何网卡。\r\n请从下面选择正确的网卡重新绑定：";
        _lblHint.Location = new Point(16, 14);
        _lblHint.Size = new Size(472, 44);
        _lblHint.ForeColor = Theme.Text;

        _lstAdapters.Location = new Point(16, 64);
        _lstAdapters.Size = new Size(472, 236);
        _lstAdapters.Font = Theme.UiFont(10f);
        _lstAdapters.DrawMode = DrawMode.OwnerDrawFixed;
        _lstAdapters.ItemHeight = 40;
        _lstAdapters.BorderStyle = BorderStyle.None;
        _lstAdapters.BackColor = Theme.Card;
        _lstAdapters.DrawItem += OnDrawAdapterItem;

        foreach (var adapter in available)
        {
            _lstAdapters.Items.Add(adapter);
        }

        if (_lstAdapters.Items.Count > 0) _lstAdapters.SelectedIndex = 0;

        _btnBind.Text = "绑定到此网卡";
        _btnBind.Location = new Point(246, 316);
        _btnBind.Size = new Size(120, 34);
        _btnBind.Tag = UiRole.Primary;
        _btnBind.Click += OnBindClick;

        _btnCancel.Text = "取消";
        _btnCancel.Location = new Point(374, 316);
        _btnCancel.Size = new Size(114, 34);
        _btnCancel.Tag = UiRole.Secondary;
        _btnCancel.DialogResult = DialogResult.Cancel;

        AcceptButton = _btnBind;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { _lblHint, _lstAdapters, _btnBind, _btnCancel });

        UiStyler.Apply(this);
        UiStyler.ScaleForDpi(this);
    }

    private void OnDrawAdapterItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _lstAdapters.Items.Count) return;
        if (_lstAdapters.Items[e.Index] is not AdapterInfo adapter) return;

        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(selected ? Theme.AccentSoft : Theme.Card))
        {
            g.FillRectangle(bg, e.Bounds);
        }

        using var mainFont = Theme.UiFont(10f, selected ? FontStyle.Bold : FontStyle.Regular);
        using var subFont = Theme.UiFont(8f);

        // 行内偏移按「行高相对设计值 40」比例换算：高 DPI 下行高会被放大到 60，
        // 若沿用硬编码的 3/21 会把两行字挤在顶部。与主窗口列表用同一套做法。
        const int designItemHeight = 40;
        var k = _lstAdapters.ItemHeight / (double)designItemHeight;

        var padX = (int)Math.Round(10 * k);
        var mainTop = (int)Math.Round(3 * k);
        var mainH = (int)Math.Round(18 * k);
        var subTop = (int)Math.Round(21 * k);
        var subH = (int)Math.Round(16 * k);

        var mainRect = new Rectangle(e.Bounds.X + padX, e.Bounds.Y + mainTop, e.Bounds.Width - padX * 2, mainH);
        TextRenderer.DrawText(g, adapter.Name, mainFont, mainRect,
            selected ? Theme.Accent : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var sub = $"#{adapter.InterfaceIndex}  {adapter.Description}";
        var subRect = new Rectangle(e.Bounds.X + padX, e.Bounds.Y + subTop, e.Bounds.Width - padX * 2, subH);
        TextRenderer.DrawText(g, sub, subFont, subRect,
            selected ? Theme.ListSubSelected : Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnBindClick(object? sender, EventArgs e)
    {
        if (_lstAdapters.SelectedItem is not AdapterInfo adapter)
        {
            MessageBox.Show(this, "请先选择一块网卡。", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SelectedAdapter = adapter.Name;
        DialogResult = DialogResult.OK;
    }
}

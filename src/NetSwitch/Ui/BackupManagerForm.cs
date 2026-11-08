using System.Drawing;
using System.Windows.Forms;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 备份记录管理器。对应旧版 <c>Show-BackupManager</c>（第 2999-3168 行）。
///
/// 备份由「应用配置集」前自动产生（backups\NetSwitch-backup-*.json，保留 10 份）。
/// 提供两种恢复方式：
///   • 导入为配置集 —— 把备份变成可编辑的配置集，之后手动应用
///   • 直接还原到网卡 —— 一步写回，需管理员权限
/// </summary>
public sealed class BackupManagerForm : Form
{
    private readonly AppServices _services;
    private readonly ListBox _lstBackups = new();
    private readonly Label _lblHint = new();
    private readonly Button _btnRestore = new();
    private readonly Button _btnRestoreDirect = new();
    private readonly Button _btnOpenDir = new();
    private readonly Button _btnClose = new();

    public BackupManagerForm(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Text = "备份记录（切换配置集前自动保存）";
        Size = new Size(620, 452);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.Bg;
        Font = Theme.UiFont(9f);

        _lblHint.Text = "每次应用配置集前会自动备份当时的网络状态，最多保留 10 份。";
        _lblHint.Location = new Point(16, 14);
        _lblHint.Size = new Size(572, 22);
        _lblHint.ForeColor = Theme.TextMuted;

        _lstBackups.Location = new Point(16, 44);
        _lstBackups.Size = new Size(572, 250);
        _lstBackups.Font = Theme.UiFont(10f);
        _lstBackups.BorderStyle = BorderStyle.None;
        _lstBackups.BackColor = Theme.Card;
        _lstBackups.IntegralHeight = false;

        _btnRestore.Text = "导入为配置集";
        _btnRestore.Location = new Point(16, 344);
        _btnRestore.Size = new Size(124, 34);
        _btnRestore.Tag = UiRole.Primary;
        _btnRestore.Click += OnImportClick;

        _btnRestoreDirect.Text = "直接还原到网卡";
        _btnRestoreDirect.Location = new Point(148, 344);
        _btnRestoreDirect.Size = new Size(132, 34);
        _btnRestoreDirect.Tag = UiRole.Secondary;
        _btnRestoreDirect.Click += OnRestoreDirectClick;

        _btnOpenDir.Text = "打开备份目录";
        _btnOpenDir.Location = new Point(288, 344);
        _btnOpenDir.Size = new Size(120, 34);
        _btnOpenDir.Tag = UiRole.Secondary;
        _btnOpenDir.Click += OnOpenDirClick;

        _btnClose.Text = "关闭";
        _btnClose.Location = new Point(492, 344);
        _btnClose.Size = new Size(96, 34);
        _btnClose.Tag = UiRole.Secondary;
        _btnClose.DialogResult = DialogResult.Cancel;

        CancelButton = _btnClose;

        Controls.AddRange(new Control[]
        {
            _lblHint, _lstBackups, _btnRestore, _btnRestoreDirect, _btnOpenDir, _btnClose,
        });

        UiStyler.Apply(this);
        UiStyler.ScaleForDpi(this);

        LoadBackups();
    }

    private void LoadBackups()
    {
        _lstBackups.Items.Clear();

        try
        {
            foreach (var file in _services.Backups.ListFiles())
            {
                _lstBackups.Items.Add(Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取备份列表失败：{ex.Message}");
        }

        if (_lstBackups.Items.Count > 0)
        {
            _lstBackups.SelectedIndex = 0;
        }
        else
        {
            _lstBackups.Items.Add("（还没有任何备份）");
            _btnRestore.Enabled = false;
            _btnRestoreDirect.Enabled = false;
        }
    }

    private string? SelectedPath()
    {
        if (_lstBackups.SelectedItem is not string name) return null;
        if (name.StartsWith("（", StringComparison.Ordinal)) return null;

        var dir = _services.Backups.BackupDir;
        var full = Path.Combine(dir, name);
        return File.Exists(full) ? full : null;
    }

    private void OnImportClick(object? sender, EventArgs e)
    {
        var path = SelectedPath();
        if (path is null) return;

        try
        {
            var added = _services.Backups.ImportAsProfiles(path, _services.Config.Data);
            if (added > 0)
            {
                _services.Config.Save();
                MessageBox.Show(this,
                    $"已从备份导入 {added} 个配置集，可在主界面看到。\r\n\r\n" +
                    "导入的配置集不会自动应用，需要你手动点「应用此配置集」。",
                    "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show(this, "该备份里没有可导入的配置集。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"导入备份失败：{ex.Message}");
            MessageBox.Show(this, $"导入备份失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRestoreDirectClick(object? sender, EventArgs e)
    {
        var path = SelectedPath();
        if (path is null) return;

        if (!Elevation.IsAdmin())
        {
            MessageBox.Show(this,
                "还原备份会直接修改网卡配置，需要管理员权限。\r\n\r\n请以管理员身份重新启动本程序后再试。",
                "NetSwitch - 需要管理员权限",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定要把备份「{Path.GetFileName(path)}」直接写回网卡吗？\r\n\r\n" +
            "这会覆盖当前网络配置。还原前会自动再备份一次当前状态。",
            "NetSwitch - 还原备份",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var ok = _services.Backups.RestoreToAdapters(path);
            Cursor = Cursors.Default;

            if (ok)
            {
                MessageBox.Show(this, "备份已还原到网卡。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show(this, "还原过程中有问题，请查看主界面日志了解详情。", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            Log.Error($"还原备份失败：{ex.Message}");
            MessageBox.Show(this, $"还原备份失败：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnOpenDirClick(object? sender, EventArgs e)
    {
        try
        {
            var dir = _services.Backups.BackupDir;
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, $"备份目录还不存在：\r\n{dir}", "NetSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"打开备份目录失败：{ex.Message}");
        }
    }
}

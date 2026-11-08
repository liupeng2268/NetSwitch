using System.Drawing;
using System.Windows.Forms;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 对话框服务。把三个模态窗口的创建集中在此，主窗体只调本服务，
/// 避免界面层散落大量 <c>new XxxForm(...)</c>。
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly AppServices _services;

    public DialogService(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public bool ShowProfileEditor(Profile? source)
    {
        using var form = new ProfileEditorForm(_services, source);
        return form.ShowDialog() == DialogResult.OK;
    }

    /// <inheritdoc />
    public void ShowBackupManager()
    {
        using var form = new BackupManagerForm(_services);
        form.ShowDialog();
    }

    /// <inheritdoc />
    public string? ShowAdapterRebind(string pattern, List<AdapterInfo> available)
    {
        using var form = new AdapterRebindForm(pattern, available);
        return form.ShowDialog() == DialogResult.OK ? form.SelectedAdapter : null;
    }
}

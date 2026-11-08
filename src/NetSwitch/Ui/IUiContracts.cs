using NetSwitch.Models;

namespace NetSwitch.Ui;

/// <summary>
/// 界面层公共契约。
///
/// 界面只依赖 <c>AppServices</c> 与这些接口/模型，不直接触碰 CIM 或文件系统。
/// </summary>
public static class UiContract
{
    /// <summary>版本号，用于窗口标题。与 csproj 的 &lt;Version&gt; 保持一致。</summary>
    public const string Version = "2.0.0";
}

/// <summary>
/// 各对话框的入口。由主窗体调用，便于集中管理模态窗口的创建与释放。
/// </summary>
public interface IDialogService
{
    /// <summary>配置集新建 / 编辑。source 为 null 表示新建。返回是否保存成功。</summary>
    bool ShowProfileEditor(Profile? source);

    /// <summary>备份记录管理器。</summary>
    void ShowBackupManager();

    /// <summary>
    /// 网卡重绑对话框：命中 0 块网卡时询问是否重新绑定。
    /// 返回重绑后的新匹配值；取消返回 null。
    /// </summary>
    string? ShowAdapterRebind(string pattern, List<AdapterInfo> available);
}

/// <summary>
/// Win11 主题自绘的控件角色，对应旧版按钮的 Tag 值。
/// </summary>
public enum UiRole
{
    /// <summary>强调色实心按钮（主干操作）。</summary>
    Primary,

    /// <summary>白底描边按钮（常规操作）。</summary>
    Secondary,

    /// <summary>白底红字按钮（删除等破坏性操作）。</summary>
    Danger,
}

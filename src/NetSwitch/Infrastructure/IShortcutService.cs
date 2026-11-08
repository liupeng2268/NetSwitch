namespace NetSwitch.Infrastructure;

/// <summary>
/// 桌面快捷方式生成 —— 对应旧版 <c>New-ProfileShortcut</c> / <c>Sync-ProfileShortcutOnRename</c>。
///
/// 旧版用 WScript.Shell COM 创建 .lnk，并设置：
///   • 目标：powershell.exe -File NetSwitch.ps1 -Apply "配置集名" -Silent
///   • 图标：netshell.dll,18（系统网络图标）
///   • 以管理员身份运行（快捷方式的 RunAs 标志位）
///
/// C# 版直接指向自身 exe，不再经过 PowerShell 转发。
/// </summary>
public interface IShortcutService
{
    /// <summary>为配置集创建桌面快捷方式。返回是否成功。</summary>
    bool CreateProfileShortcut(string profileName);

    /// <summary>配置集改名后同步重命名对应的快捷方式（存在才处理）。</summary>
    void SyncOnRename(string oldName, string newName);

    /// <summary>删除配置集时一并删除其快捷方式。</summary>
    void RemoveProfileShortcut(string profileName);
}

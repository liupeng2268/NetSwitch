using System.Runtime.InteropServices;
using System.Text;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 桌面快捷方式生成 —— 对应旧版 <c>New-ProfileShortcut</c> / <c>Sync-ProfileShortcutOnRename</c>
/// （脚本第 1368-1416 行）。
///
/// 旧版用 WScript.Shell COM 创建 .lnk，C# 版改用 <c>IShellLink</c> + <c>IPersistFile</c>
/// COM 互操作直接生成，<b>不引入任何 PowerShell 依赖</b>，也不新增 NuGet 包。
///
/// 复刻的行为要点：
///   • 目标 = 当前 exe（<see cref="Environment.ProcessPath"/>），参数 = <c>-Apply "名称" -Silent</c>
///   • 说明 = <c>一键切换到 [名称]</c>；工作目录 = 程序主目录；图标 = netshell.dll,18
///   • 文件名 = <c>NetSwitch-{安全名}.lnk</c>，放在用户桌面
///   • 手工把 .lnk 文件偏移 0x15 的第 5 位置 1（LinkFlags 的 RunAsUser 位），
///     双击即提权，省掉二次确认 —— 与旧版逐位一致
/// </summary>
public sealed class ShortcutService : IShortcutService
{
    /// <summary>旧版脚本名常量 <c>$script:AppName</c>，用于拼快捷方式文件名。</summary>
    private const string AppName = "NetSwitch";

    // WScript.Shell / IShellLink 常量
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private const string IconLocationPath = "netshell.dll";
    private const int IconLocationIndex = 18;

    /// <summary>.lnk 文件里「以管理员身份运行」标志位所在的字节偏移（LinkFlags 第二字节）。</summary>
    private const int RunAsFlagOffset = 0x15;

    /// <summary>该字节上 RunAsUser 对应的位（LinkFlags 的 0x2000，落在低字节之后那一位）。</summary>
    private const byte RunAsFlagBit = 0x20;

    /// <inheritdoc />
    public bool CreateProfileShortcut(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;

        try
        {
            var linkPath = BuildLinkPath(profileName);
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Log.Warn("创建快捷方式失败：无法定位当前可执行文件路径。");
                return false;
            }

            var shellLink = (IShellLinkW)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ShellLinkClsid)!)!;

            shellLink.SetPath(exePath);
            shellLink.SetArguments($"-Apply \"{profileName}\" -Silent");
            shellLink.SetWorkingDirectory(Paths.Home);
            shellLink.SetDescription($"一键切换到 [{profileName}]");
            shellLink.SetIconLocation(IconLocationPath, IconLocationIndex);

            ((IPersistFile)shellLink).Save(linkPath, true);

            // 给 .lnk 打上「以管理员身份运行」标志位
            SetRunAsFlag(linkPath);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"创建快捷方式失败：{ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public void SyncOnRename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName)) return;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

        try
        {
            var oldLink = BuildLinkPath(oldName);
            if (!File.Exists(oldLink)) return;

            // 先按新名称生成，确认成功后再删旧链；避免中途失败反而把可用的快捷方式弄丢
            if (!CreateProfileShortcut(newName)) return;

            if (File.Exists(oldLink))
            {
                File.Delete(oldLink);
            }

            Log.Ok($"配置集已改名，桌面快捷方式同步更新为「{Path.GetFileName(BuildLinkPath(newName))}」");
        }
        catch (Exception ex)
        {
            Log.Warn($"同步桌面快捷方式失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void RemoveProfileShortcut(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        try
        {
            var linkPath = BuildLinkPath(profileName);
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除桌面快捷方式失败：{ex.Message}");
        }
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────

    /// <summary>桌面上的快捷方式完整路径：<c>{桌面}\NetSwitch-{安全名}.lnk</c>（对应旧版命名）。</summary>
    private static string BuildLinkPath(string profileName)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var safeName = ToSafeFileName(profileName);
        return Path.Combine(desktop, $"{AppName}-{safeName}.lnk");
    }

    /// <summary>把文件系统非法字符替换为下划线，与旧版 <c>-replace '[\\/:*?\"&lt;&gt;|]', '_'</c> 一致。</summary>
    private static string ToSafeFileName(string name)
    {
        var invalid = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        return builder.ToString();
    }

    /// <summary>把 .lnk 文件偏移 0x15 的字节按位或上 0x20，即设置 RunAsUser 标志。</summary>
    private static void SetRunAsFlag(string linkPath)
    {
        var bytes = File.ReadAllBytes(linkPath);
        bytes[RunAsFlagOffset] = (byte)(bytes[RunAsFlagOffset] | RunAsFlagBit);
        File.WriteAllBytes(linkPath, bytes);
    }
}

// ============================================================================
// IShellLink / IPersistFile 的 COM 互操作声明
//
// 这两个接口的方法顺序必须与系统 vtable 严格一致，否则调用会跑到错误地址。
// 由于 vtable 顺序按接口定义整体排布、不能只挑用得到的方法，因此必须把
// 「到目标方法为止」的全部方法按原始顺序列出（未用到的用占位签名）。
// ============================================================================

/// <summary>IShellLinkW（IID 000214F9-...）。方法顺序即 vtable 顺序，勿改动。</summary>
[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

/// <summary>IPersistFile（IID 0000010b-...），用于把 IShellLink 落到磁盘。</summary>
[ComImport]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    // 继承自 IPersist
    void GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

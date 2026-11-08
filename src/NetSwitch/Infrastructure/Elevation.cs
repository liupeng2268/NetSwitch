using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using NetSwitch.Models;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 权限与提权。行为与旧版 <c>Test-IsAdmin</c> / <c>Start-Elevated</c> 对齐。
///
/// 旧版用 <c>Start-Process powershell -Verb RunAs</c> 重启脚本并靠 <c>$script:ScriptArgs</c>
/// 快照转发参数（函数内 <c>$PSBoundParameters</c> 恒为空，是个踩过的坑）。
/// C# 版直接用 <c>Verb = "runas"</c> 重启自身 exe，参数从 AppOptions 重新拼装，
/// 同样追加 <c>-Elevated</c> 标记避免循环提权。
/// </summary>
public static class Elevation
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员权限重启自身，转发原有参数。
    /// 用户取消 UAC 时返回 false（不抛异常），调用方据此决定是否退出。
    /// </summary>
    public static bool RestartElevated(AppOptions options)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments = BuildForwardArguments(options),
            };

            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗上点了「否」
            Log.Warn("提权已取消（用户拒绝了 UAC 授权）。");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"提权失败：{ex.Message}（用户可能取消了 UAC 授权）");
            return false;
        }
    }

    /// <summary>
    /// 拼装转发给提权进程的命令行参数。
    /// 跳过 <c>Elevated</c>（最后统一追加），只转发确实有值的项。
    /// </summary>
    private static string BuildForwardArguments(AppOptions options)
    {
        var parts = new List<string>();

        void AddValue(string flag, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(flag);
                parts.Add(Quote(value));
            }
        }

        void AddSwitch(string flag, bool on)
        {
            if (on) parts.Add(flag);
        }

        AddValue("-Apply", options.Apply);
        AddSwitch("-ListProfiles", options.ListProfiles);
        AddSwitch("-ListAdapters", options.ListAdapters);
        AddSwitch("-ShowStatus", options.ShowStatus);
        AddValue("-ImportFile", options.ImportFile);
        AddValue("-ExportFile", options.ExportFile);
        AddSwitch("-Silent", options.Silent);
        AddValue("-ConfigPath", options.ConfigPath);

        parts.Add("-Elevated");
        return string.Join(' ', parts);
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;
}

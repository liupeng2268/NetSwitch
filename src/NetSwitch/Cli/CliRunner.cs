using System.Text;
using NetSwitch.Infrastructure;
using NetSwitch.Models;
using NetSwitch.Ui;

namespace NetSwitch.Cli;

/// <summary>
/// 命令行模式。逐分支对应旧版 <c>Invoke-Cli</c>（第 1635-1745 行）。
///
/// 输出文案与旧版逐字一致 —— 这是新旧版逐字比对验证的基础。
/// </summary>
public sealed class CliRunner : ICliRunner
{
    /// <inheritdoc />
    public int Run(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.Options;

        if (options.ListAdapters) return ListAdapters(services);
        if (options.ListProfiles) return ListProfiles(services);
        if (options.ShowStatus) return ShowStatus(services);
        if (!string.IsNullOrWhiteSpace(options.ExportFile)) return ExportFile(services, options.ExportFile);
        if (!string.IsNullOrWhiteSpace(options.ImportFile)) return ImportFile(services, options.ImportFile);
        if (!string.IsNullOrWhiteSpace(options.Apply)) return ApplyProfile(services, options.Apply);

        PrintUsage();
        return 0;
    }

    // ── 各分支 ────────────────────────────────────────────────────────────

    private static int ListAdapters(AppServices services)
    {
        Console.WriteLine();
        WriteColored("本机网卡列表：", ConsoleColor.Cyan);

        List<AdapterInfo> list;
        try
        {
            list = services.Adapters.GetAdapters();
        }
        catch (Exception ex)
        {
            Log.Error($"读取网卡列表失败：{ex.Message}");
            return 1;
        }

        // 简易表格式输出，列宽按内容自适应
        var rows = list
            .Select(a => new[]
            {
                a.InterfaceIndex.ToString(),
                a.Name,
                a.Up ? "Up" : "Disconnected",
                a.MacAddress,
            })
            .ToList();

        PrintTable(new[] { "索引", "名称", "状态", "MAC" }, rows);
        return 0;
    }

    private static int ListProfiles(AppServices services)
    {
        // 旧版为 Write-Host "`n已配置的配置集："：只有一个前导空行，标题后不空行
        Console.WriteLine();
        WriteColored("已配置的配置集：", ConsoleColor.Cyan);

        var profiles = services.Config.Data.Profiles;
        if (profiles.Count == 0)
        {
            Console.WriteLine("  （当前没有任何配置集）");
            return 0;
        }

        foreach (var profile in profiles)
        {
            WriteColored($"  - {profile.Name}", ConsoleColor.White, newline: false);

            if (!string.IsNullOrWhiteSpace(profile.Remark))
            {
                WriteColored($"    {profile.Remark}", ConsoleColor.DarkGray);
            }
            else
            {
                Console.WriteLine();
            }

            foreach (var adapter in profile.Adapters)
            {
                var modeText = adapter.Mode == "dhcp"
                    ? "DHCP"
                    : $"{adapter.Ip} mask:{adapter.Mask} gw:{string.Join(",", adapter.Gateways ?? new List<string>())}";

                var display = services.Adapters.GetDisplayText(adapter.Adapter);
                WriteColored($"      [{display}] {modeText}", ConsoleColor.Gray);
            }
        }

        return 0;
    }

    private static int ShowStatus(AppServices services)
    {
        Console.WriteLine();
        WriteColored("当前网络状态：", ConsoleColor.Cyan);
        Console.WriteLine();
        Console.Write(services.Status.GetStatusText());
        return 0;
    }

    private static int ExportFile(AppServices services, string target)
    {
        try
        {
            if (services.Transfer.ExportFile(target, services.Config.Data))
            {
                WriteColored($"配置已导出到：{target}", ConsoleColor.Green);
                return 0;
            }

            WriteColored($"配置导出失败：{target}", ConsoleColor.Red);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error($"导出配置失败：{ex.Message}");
            WriteColored($"配置导出失败：{ex.Message}", ConsoleColor.Red);
            return 1;
        }
    }

    private static int ImportFile(AppServices services, string source)
    {
        if (!File.Exists(source))
        {
            WriteColored($"文件不存在：{source}", ConsoleColor.Red);
            return 1;
        }

        try
        {
            if (services.Transfer.ImportFile(source, services.Config))
            {
                WriteColored($"已导入到配置文件：{services.Config.ConfigPath}", ConsoleColor.Green);
                return 0;
            }

            WriteColored($"导入失败：{source}", ConsoleColor.Red);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error($"导入配置失败：{ex.Message}");
            WriteColored($"导入配置失败：{ex.Message}", ConsoleColor.Red);
            return 1;
        }
    }

    private static int ApplyProfile(AppServices services, string requestedName)
    {
        var options = services.Options;

        // 1) 权限检查与提权
        if (!Elevation.IsAdmin())
        {
            if (!options.Elevated)
            {
                WriteColored("需要管理员权限，正在请求 UAC 提权…", ConsoleColor.Yellow);

                if (Elevation.RestartElevated(options))
                {
                    // 提权进程已接手，本进程结束
                    return 0;
                }
            }

            WriteColored("未获得管理员权限，操作取消。", ConsoleColor.Red);
            ShowCliMessageBox(
                "未能获得管理员权限，网络配置没有做任何改动。",
                "NetSwitch - 操作取消",
                MessageBoxIcon.Error);
            return 1;
        }

        // 2) 解析配置集名（宽松匹配：首尾空格、大小写）
        var actualName = ResolveProfileName(services, requestedName);
        if (actualName is null)
        {
            var names = services.Profiles.GetNameList();
            var body = names.Count > 0 ? string.Join("\n  ", names) : "（当前没有任何配置集）";
            var msg = $"找不到配置集：「{requestedName}」\n\n当前可用的配置集：\n  {body}\n\n" +
                      "如果刚给配置集改过名字，回主界面点「快捷方式」重新生成一个即可。";

            Log.Error($"找不到配置集「{requestedName}」");
            WriteColored(msg, ConsoleColor.Red);
            ShowCliMessageBox(msg, "NetSwitch - 找不到配置集", MessageBoxIcon.Warning);
            return 1;
        }

        // 3) 应用
        var result = services.Engine.Apply(actualName);
        if (!result)
        {
            var msg = $"配置集「{actualName}」没有切换成功，网络可能未按预期生效。\n\n" +
                      $"日志文件：{Log.LogFile}";

            WriteColored(msg, ConsoleColor.Red);
            ShowCliMessageBox(msg, "NetSwitch - 切换未完成", MessageBoxIcon.Warning);
            return 1;
        }

        // 4) 成功后打印状态
        Console.WriteLine();
        WriteColored("切换后的网络状态：", ConsoleColor.Cyan);
        Console.WriteLine();
        Console.Write(services.Status.GetStatusText());

        if (!options.Silent)
        {
            WriteColored("按任意键结束…", ConsoleColor.DarkGray);
            try
            {
                if (ConsoleBridge.IsAttached) Console.ReadKey(intercept: true);
            }
            catch
            {
                // 无控制台时忽略
            }
        }

        return 0;
    }

    /// <summary>宽松解析配置集名；找不到返回 null。</summary>
    private static string? ResolveProfileName(AppServices services, string requested)
    {
        var trimmed = requested.Trim();
        var match = services.Profiles.FindByName(trimmed);
        return match?.Name;
    }

    // ── 输出辅助 ──────────────────────────────────────────────────────────

    private static void PrintUsage()
    {
        Console.WriteLine();
        WriteColored($"NetSwitch {UiContract.Version} - 用法提示", ConsoleColor.Cyan);
        Console.WriteLine("  -Apply \"名称\"     应用指定配置集（可配合 -Silent 用于快捷方式）");
        Console.WriteLine("  -ListProfiles    列出全部配置集");
        Console.WriteLine("  -ListAdapters    列出本机网卡名称（用于填写配置）");
        Console.WriteLine("  -ShowStatus      查看当前网络状态");
        Console.WriteLine("  -ExportFile x    导出配置");
        Console.WriteLine("  -ImportFile x    导入配置");
        Console.WriteLine("不带参数运行即为图形界面模式。");
        Console.WriteLine();
    }

    private static void WriteColored(string text, ConsoleColor color, bool newline = true)
    {
        if (!ConsoleBridge.IsAttached)
        {
            // 无控制台时不尝试上色，退化为普通输出（可能无接收方，属于正常）
            if (newline) Console.WriteLine(text); else Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            if (newline) Console.WriteLine(text); else Console.Write(text);
        }
        catch
        {
            if (newline) Console.WriteLine(text); else Console.Write(text);
        }
        finally
        {
            try { Console.ForegroundColor = previous; } catch { }
        }
    }

    /// <summary>
    /// 简易等宽表格。替代 PowerShell 的 <c>Format-Table -AutoSize</c>：
    /// 列宽按最长内容自适应，中文按 2 个字符宽度计算以保持对齐。
    /// </summary>
    private static void PrintTable(string[] headers, List<string[]> rows)
    {
        var columnCount = headers.Length;
        var widths = new int[columnCount];

        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < columnCount && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
            }
        }

        var headerLine = new StringBuilder();
        for (var i = 0; i < columnCount; i++)
        {
            headerLine.Append(PadRight(headers[i], widths[i]));
            if (i < columnCount - 1) headerLine.Append("  ");
        }
        Console.WriteLine(headerLine.ToString());

        var separator = new StringBuilder();
        for (var i = 0; i < columnCount; i++)
        {
            separator.Append(new string('-', widths[i]));
            if (i < columnCount - 1) separator.Append("  ");
        }
        Console.WriteLine(separator.ToString());

        foreach (var row in rows)
        {
            var line = new StringBuilder();
            for (var i = 0; i < columnCount; i++)
            {
                line.Append(PadRight(i < row.Length ? row[i] : "", widths[i]));
                if (i < columnCount - 1) line.Append("  ");
            }
            Console.WriteLine(line.ToString());
        }
    }

    /// <summary>计算显示宽度：CJK 字符占 2 列。</summary>
    private static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var width = 0;
        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }
        return width;
    }

    private static string PadRight(string? text, int targetWidth)
    {
        text ??= "";
        var current = DisplayWidth(text);
        return current >= targetWidth ? text : text + new string(' ', targetWidth - current);
    }

    private static bool IsWide(char ch)
        => ch >= 0x1100 && (
               ch <= 0x115F                            // 韩文字母
            || ch is >= (char)0x2E80 and <= (char)0xA4CF   // CJK 部首、汉字、假名
            || ch is >= (char)0xAC00 and <= (char)0xD7A3   // 韩文音节
            || ch is >= (char)0xF900 and <= (char)0xFAFF   // 兼容汉字
            || ch is >= (char)0xFE30 and <= (char)0xFE6F   // CJK 兼容形式
            || ch is >= (char)0xFF00 and <= (char)0xFF60   // 全角形式
            || ch is >= (char)0xFFE0 and <= (char)0xFFE6);

    /// <summary>
    /// 命令行模式下的可见反馈。桌面快捷方式用隐藏窗口启动时，控制台文字用户看不到，
    /// 出错必须弹窗，否则用户只会觉得「双击没反应」（旧版 Show-CliMessageBox 的用意）。
    /// </summary>
    private static void ShowCliMessageBox(string message, string title, MessageBoxIcon icon)
    {
        try
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }
        catch
        {
            // 无图形环境时退化为控制台输出
            Console.WriteLine(message);
        }
    }
}

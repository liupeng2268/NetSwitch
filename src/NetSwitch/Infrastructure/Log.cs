using System.Text;
using NetSwitch.Models;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 日志。三个出口与旧版一致：落盘（始终）→ 界面日志框（若已绑定）→ 控制台（仅 CLI 模式）。
///
/// 输出格式复刻旧版，保证新旧日志可直接比对：
///   <code>[2026-11-08 05:20:00] [ √ ] 配置集「门诊楼」已生效（共处理 1 块网卡）</code>
/// 级别前缀：INFO → <c>[ - ]</c>，OK → <c>[ √ ]</c>，WARN → <c>[ ! ]</c>，ERROR → <c>[ X ]</c>。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>日志文件绝对路径；空表示未启用落盘。</summary>
    public static string LogFile { get; private set; } = "";

    /// <summary>是否命令行模式（决定是否输出到控制台）。</summary>
    public static bool IsCliMode { get; set; }

    /// <summary>界面日志框的接收器；由主窗体在创建后注册。null 表示无界面。</summary>
    public static Action<string, LogLevel>? UiSink { get; set; }

    private const long MaxLogBytes = 1024 * 1024;   // 1 MB
    private const int KeepTailLines = 500;

    /// <summary>
    /// 定位可写日志文件并写启动快照。候选依次尝试，取第一个可写的；
    /// 全部不可写则关闭落盘（功能不受影响）。
    /// </summary>
    public static void Initialize(string configPath, string version)
    {
        LogFile = "";

        foreach (var candidate in Paths.GetLogPathCandidates())
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 试探写入，确认确实可写
                File.AppendAllText(candidate, string.Empty);
                LogFile = candidate;
                break;
            }
            catch
            {
                // 换下一个候选
            }
        }

        TrimIfTooLarge();

        // 启动快照：只落盘，不刷屏。
        // 旧版的 Write-NSLog 在 CLI 模式下也会打到控制台，导致 `-ListProfiles`
        // 输出被 7 行环境信息淹没。这里改为「仅写文件」，让 CLI 输出保持干净——
        // 排查时仍可从 NetSwitch.log 拿到同样的信息。
        WriteStartupSnapshot(configPath, version);
    }

    /// <summary>写启动快照到日志文件（不输出到控制台/界面）。</summary>
    private static void WriteStartupSnapshot(string configPath, string version)
    {
        var lines = new[]
        {
            "------------------------------------------------------------",
            $"NetSwitch {version} 启动",
            $"环境: {Environment.OSVersion.VersionString} / .NET {Environment.Version}",
            $"用户: {Environment.UserName} / 已获取管理员权限: {Elevation.IsAdmin()}",
            $"程序路径: {Environment.ProcessPath}",
            $"配置文件: {configPath}",
            $"日志文件: {(string.IsNullOrWhiteSpace(LogFile) ? "（无可用写入位置，未启用落盘）" : LogFile)}",
        };

        foreach (var text in lines)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ - ] {text}";
            lock (Gate)
            {
                AppendToFile(line);
            }
        }
    }

    /// <summary>日志超限时只保留最近若干行，避免长期无限增长。</summary>
    private static void TrimIfTooLarge()
    {
        if (string.IsNullOrWhiteSpace(LogFile) || !File.Exists(LogFile)) return;
        try
        {
            if (new FileInfo(LogFile).Length <= MaxLogBytes) return;

            var lines = File.ReadAllLines(LogFile, Encoding.UTF8);
            var tail = lines.Length > KeepTailLines
                ? lines[^KeepTailLines..]
                : lines;

            File.WriteAllLines(LogFile, tail, new UTF8Encoding(true));
        }
        catch
        {
            // 轮转失败不影响主流程
        }
    }

    // ── 对外写入口 ────────────────────────────────────────────────────────

    public static void Info(string message)  => Write(message, LogLevel.Info);
    public static void Ok(string message)    => Write(message, LogLevel.Ok);
    public static void Warn(string message)  => Write(message, LogLevel.Warn);
    public static void Error(string message) => Write(message, LogLevel.Error);

    public static void Write(string message, LogLevel level)
    {
        var prefix = level switch
        {
            LogLevel.Ok    => "[ √ ]",
            LogLevel.Warn  => "[ ! ]",
            LogLevel.Error => "[ X ]",
            _              => "[ - ]",
        };

        var plain = $"{prefix} {message}";
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {plain}";

        lock (Gate)
        {
            AppendToFile(line);

            var sink = UiSink;
            if (sink is not null)
            {
                try { sink(line, level); }
                catch { Console.WriteLine(line); }
            }
            else if (IsCliMode)
            {
                WriteConsoleColored(line, level);
            }
        }
    }

    private static void AppendToFile(string line)
    {
        if (string.IsNullOrWhiteSpace(LogFile)) return;
        try
        {
            FileIo.AppendLine(LogFile, line);
        }
        catch
        {
            // 日志不可写不代表功能不可用
        }
    }

    private static void WriteConsoleColored(string line, LogLevel level)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = level switch
            {
                LogLevel.Ok    => ConsoleColor.Green,
                LogLevel.Warn  => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _              => ConsoleColor.Gray,
            };
            Console.WriteLine(line);
        }
        catch
        {
            Console.WriteLine(line);
        }
        finally
        {
            try { Console.ForegroundColor = previous; } catch { }
        }
    }

    /// <summary>取最近 N 行日志，用于「复制日志」按钮。</summary>
    public static string GetTail(int lines = 300)
    {
        if (string.IsNullOrWhiteSpace(LogFile) || !File.Exists(LogFile)) return "";
        try
        {
            var all = File.ReadAllLines(LogFile, Encoding.UTF8);
            var tail = all.Length > lines ? all[^lines..] : all;
            return string.Join("\r\n", tail);
        }
        catch
        {
            return "";
        }
    }
}

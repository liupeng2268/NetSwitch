namespace NetSwitch.Infrastructure;

/// <summary>
/// 路径定位。所有目录查找集中在此，避免各模块各写一套。
///
/// 「脚本目录」在 C# 里没有 <c>$PSScriptRoot</c> 的等价物，判定顺序取：
///   1. 环境变量 <c>NETSWITCH_HOME</c>（供测试与便携部署覆盖）
///   2. 可执行文件所在目录（单文件发布时即解包目录的同级）
///   3. 当前工作目录
/// 这样单文件 exe 放在哪，配置就跟着放哪，与旧版「脚本同目录优先」的体验一致。
/// </summary>
public static class Paths
{
    /// <summary>程序主目录，配置文件与日志的默认位置。</summary>
    public static string Home { get; } = ResolveHome();

    private static string ResolveHome()
    {
        var env = Environment.GetEnvironmentVariable("NETSWITCH_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env.TrimEnd(Path.DirectorySeparatorChar);
        }

        // AppContext.BaseDirectory 在单文件发布下指向解包临时目录，
        // 因此优先用进程主模块路径，取到的才是 exe 真实所在目录。
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    return dir.TrimEnd(Path.DirectorySeparatorChar);
                }
            }
        }
        catch { /* 落回工作目录 */ }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>默认配置文件路径：主目录 profiles.json 优先，否则 %APPDATA%\NetSwitch。</summary>
    public static string GetDefaultConfigPath()
    {
        var local = Path.Combine(Home, "profiles.json");
        var roaming = Path.Combine(GetAppDataDir(), "profiles.json");

        if (File.Exists(local)) return local;
        if (File.Exists(roaming)) return roaming;
        return local;
    }

    /// <summary>%APPDATA%\NetSwitch。</summary>
    public static string GetAppDataDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetSwitch");

    /// <summary>日志文件候选位置（按优先级）。与旧版一致：主目录 → APPDATA → 临时目录。</summary>
    public static IEnumerable<string> GetLogPathCandidates()
    {
        yield return Path.Combine(Home, "NetSwitch.log");
        yield return Path.Combine(GetAppDataDir(), "NetSwitch.log");
        yield return Path.Combine(Path.GetTempPath(), "NetSwitch.log");
    }

    /// <summary>切换前备份目录：与配置文件同级。</summary>
    public static string GetBackupDir(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(dir)) dir = Home;
        return Path.Combine(dir, "backups");
    }

    /// <summary>已应用路由记录文件（applied-routes.json）候选位置。</summary>
    public static IEnumerable<string> GetAppliedRoutesPathCandidates(string configPath)
    {
        yield return Path.Combine(Home, "applied-routes.json");

        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            yield return Path.Combine(configDir, "applied-routes.json");
        }

        yield return Path.Combine(Path.GetTempPath(), "NetSwitch-applied-routes.json");
    }
}

using System.Text.RegularExpressions;
using NetSwitch.Infrastructure;

namespace NetSwitch.Config;

/// <summary>
/// 配置文件自身的轮转备份 —— 对应旧版 <c>Backup-ConfigFile</c>（脚本第 426-460 行）。
///
/// 覆盖写之前先把现有 profiles.json 复制一份（{stem}.bak-yyyyMMdd-HHmmss.json），
/// 写坏或误删时可回滚。任何失败都只记 WARN，<b>绝不影响保存主流程</b>。
/// </summary>
public sealed class ConfigBackup : IConfigBackup
{
    /// <summary>profiles.json 自身备份的保留份数，超出的旧备份会被轮转删除。</summary>
    private const int ConfigBackupKeepCount = 5;

    /// <summary>
    /// 清理时使用的严格时间戳正则模板：<c>^{stem}\.bak-\d{8}-\d{6}\.json$</c>。
    /// 旧版注释明确说明：只清理本程序自己生成的备份，不用宽泛通配符，避免误伤同目录里
    /// 名字相近的其他文件。因此这里同样采用严格匹配而非 <c>{stem}.bak-*.json</c>。
    /// </summary>
    private const string BackupPatternFormat = @"^{0}\.bak-\d{{8}}-\d{{6}}\.json$";

    /// <inheritdoc />
    public void BackupBeforeSave(string configPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath)) return;
            if (!File.Exists(configPath)) return;

            var directory = Path.GetDirectoryName(configPath);
            if (string.IsNullOrWhiteSpace(directory)) directory = Paths.Home;

            var stem = Path.GetFileNameWithoutExtension(configPath);
            var target = Path.Combine(
                directory,
                $"{stem}.bak-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            File.Copy(configPath, target, overwrite: true);

            // 只清理严格匹配 {stem}.bak-yyyyMMdd-HHmmss.json 的旧备份，保留最新 5 份
            var pattern = new Regex(
                string.Format(BackupPatternFormat, Regex.Escape(stem)),
                RegexOptions.CultureInvariant);

            var existing = Directory
                .EnumerateFiles(directory)
                .Where(path => pattern.IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();

            if (existing.Count > ConfigBackupKeepCount)
            {
                foreach (var old in existing.Skip(ConfigBackupKeepCount))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"删除过期配置备份失败（{old}）：{ex.Message}");
                    }
                }
            }

            Log.Ok($"已备份现有配置到 {target}");
        }
        catch (Exception ex)
        {
            Log.Warn($"备份现有配置失败（不影响本次保存）：{ex.Message}");
        }
    }
}

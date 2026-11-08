using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 配置文件（profiles.json）读写门面。
/// 取代旧版的 Get-DefaultConfigPath / Import-ConfigFile / Save-ConfigFile /
/// Backup-ConfigFile / Repair-ConfigData 组合。
/// </summary>
public interface IConfigStore
{
    /// <summary>配置文件绝对路径。</summary>
    string ConfigPath { get; }

    /// <summary>内存中的配置数据（已修复，可安全直读）。</summary>
    ConfigFile Data { get; }

    /// <summary>
    /// 从磁盘加载。文件不存在或解析失败时回退默认配置并记日志（不抛异常），
    /// 与旧版 Import-ConfigFile 的容错行为一致。
    /// </summary>
    void Load();

    /// <summary>写回磁盘。写前自动备份（BackupConfigFile 语义）。</summary>
    void Save();
}

/// <summary>配置文件自身备份的轮转（profiles.bak-yyyyMMdd-HHmmss.json，保留 5 份）。</summary>
public interface IConfigBackup
{
    /// <summary>
    /// 把现有配置文件复制一份再让主流程覆盖写。
    /// 任何失败只记 WARN，绝不影响保存主流程。
    /// </summary>
    void BackupBeforeSave(string configPath);
}

/// <summary>网络状态备份（backups\NetSwitch-backup-*.json，保留 10 份）。</summary>
public interface IBackupService
{
    /// <summary>备份目录。</summary>
    string BackupDir { get; }

    /// <summary>采集当前各网卡状态，组装成与 profiles.json 同构的配置集并写文件。失败返回空串。</summary>
    string SaveCurrentNetwork();

    /// <summary>列出备份文件（按文件名倒序，新的在前）。</summary>
    List<string> ListFiles();

    /// <summary>把备份解析成配置集并追加到当前配置（重名自动加「 (n)」后缀）。返回新增条数。</summary>
    int ImportAsProfiles(string backupPath, ConfigFile target);

    /// <summary>把备份直接写回网卡（不必先导入）。要求管理员权限。</summary>
    bool RestoreToAdapters(string backupPath);
}

/// <summary>配置集的导入 / 导出（整份文件）。对应 CLI 的 -ImportFile / -ExportFile。</summary>
public interface IConfigTransfer
{
    /// <summary>从文件导入并覆盖当前配置（先备份），返回是否成功。</summary>
    bool ImportFile(string sourcePath, IConfigStore store);

    /// <summary>把当前配置导出到指定路径。</summary>
    bool ExportFile(string targetPath, ConfigFile data);
}

using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 配置文件读写门面 —— 对应旧版 <c>Get-DefaultConfigPath</c> / <c>Import-ConfigFile</c> /
/// <c>Save-ConfigFile</c>（脚本第 266-275、399-419、462-483 行）。
///
/// 读：文件不存在或解析失败都回退默认配置并修复，绝不抛异常（旧版的容错策略）。
/// 写：覆盖前先做自身备份轮转，再刷新 updatedAt，最后带 BOM 写入。
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly IConfigBackup _backup;

    /// <summary>内存中的配置数据（已修复）。构造时先按默认值占位，需调用 <see cref="Load"/> 才落定。</summary>
    private ConfigFile _data = ConfigRepair.NewDefaultData();

    /// <summary>
    /// 构造。<paramref name="configPath"/> 为空时退化为 <see cref="Paths.GetDefaultConfigPath"/>。
    /// </summary>
    /// <param name="configPath">配置文件绝对路径；为空则取默认路径。</param>
    /// <param name="backup">自身备份轮转服务。</param>
    public ConfigStore(string? configPath, IConfigBackup backup)
    {
        ConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? Paths.GetDefaultConfigPath()
            : configPath;

        _backup = backup;
    }

    /// <inheritdoc />
    public string ConfigPath { get; }

    /// <inheritdoc />
    public ConfigFile Data => _data;

    /// <inheritdoc />
    public void Load()
    {
        // 文件不存在不是错误：首次运行属正常，直接给默认配置（旧版同样不记日志）
        if (!File.Exists(ConfigPath))
        {
            _data = ConfigRepair.Repair(ConfigRepair.NewDefaultData());
            return;
        }

        var raw = PsJson.TryReadAllText(ConfigPath);
        var parsed = PsJson.TryDeserialize<ConfigFile>(raw);
        if (parsed is null)
        {
            // 文案照旧版 Import-ConfigFile 的 catch 分支：
            // 「配置文件解析失败（路径）：原因，已使用默认配置启动。」
            Log.Error($"配置文件解析失败（{ConfigPath}）：{DescribeFailure(raw)}，已使用默认配置启动。");
            _data = ConfigRepair.Repair(ConfigRepair.NewDefaultData());
            return;
        }

        _data = ConfigRepair.Repair(parsed);
    }

    /// <summary>
    /// 取解析失败的原因文本。<see cref="PsJson.TryDeserialize{T}"/> 按契约吞掉异常并返回 null，
    /// 这里为还原旧版日志里的「原因」再做一次解析以取得异常消息；纯粹用于日志，不影响主流程。
    /// </summary>
    private static string DescribeFailure(string? raw)
    {
        if (raw is null) return "文件无法读取";

        try
        {
            System.Text.Json.JsonSerializer.Deserialize<ConfigFile>(raw);
            return "文件内容不是合法的 JSON";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            Log.Error("未指定配置文件路径。");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 覆盖写之前先给现有配置留一份备份，写坏时可直接回滚
            _backup.BackupBeforeSave(ConfigPath);

            _data.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var json = PsJson.Serialize(_data);

            // 带 BOM 写入，保证 Windows 记事本打开不乱码
            FileIo.WriteAllTextWithBom(ConfigPath, json);

            Log.Ok($"配置已保存到 {ConfigPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置失败（{ConfigPath}）：{ex.Message}");
        }
    }
}

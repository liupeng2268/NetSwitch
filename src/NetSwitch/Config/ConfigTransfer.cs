using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 配置的导入 / 导出（整份文件）—— 对应旧版 CLI 的 <c>-ImportFile</c> / <c>-ExportFile</c>
/// 分支（脚本第 1675-1689 行）与 <c>Save-ConfigFile</c> 的写出逻辑。
///
/// 导入：读源文件 → 覆盖内存数据 → 落盘（<see cref="IConfigStore.Save"/> 会顺带做备份）。
/// 导出：把整份数据按 PS 风格 JSON 带 BOM 写到目标路径。
/// </summary>
public sealed class ConfigTransfer : IConfigTransfer
{
    /// <inheritdoc />
    public bool ImportFile(string sourcePath, IConfigStore store)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            Log.Error($"文件不存在：{sourcePath}");
            return false;
        }

        var raw = PsJson.TryReadAllText(sourcePath);
        var parsed = PsJson.TryDeserialize<ConfigFile>(raw);
        if (parsed is null)
        {
            Log.Error($"导入失败：配置文件解析失败（{sourcePath}），未做任何改动。");
            return false;
        }

        var repaired = ConfigRepair.Repair(parsed);

        // 覆盖内存数据：ConfigFile 的字段逐个替换，store.Data 的引用保持不变
        store.Data.Version = repaired.Version;
        store.Data.UpdatedAt = repaired.UpdatedAt;
        store.Data.Profiles = repaired.Profiles;

        store.Save();
        return true;
    }

    /// <inheritdoc />
    public bool ExportFile(string targetPath, ConfigFile data)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            Log.Error("未指定导出目标路径。");
            return false;
        }

        try
        {
            // 与旧版 Save-ConfigFile 一致：导出前刷新 updatedAt
            data.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var json = PsJson.Serialize(data);

            // 带 BOM 写入，保证 Windows 记事本打开不乱码
            FileIo.WriteAllTextWithBom(targetPath, json);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"导出配置失败（{targetPath}）：{ex.Message}");
            return false;
        }
    }
}

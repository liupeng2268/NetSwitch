using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 配置集的增删改查与排序 —— 对应旧版散落在 GUI 事件里的 <c>$btnNew</c> / <c>$btnCopy</c> /
/// <c>$btnDelete</c> / <c>$moveProfile</c> 内联块，以及 <c>Resolve-ProfileName</c> /
/// <c>Get-ProfileNameList</c> / <c>Get-ProfileByName</c>（脚本第 2358-2390、3602-3662 行）。
///
/// C# 版把这些操作集中到一处，使「改名时同步快捷方式」「删除时清理引用」等副作用有统一落点。
/// 所有写操作只改内存列表并返回结果，是否落盘由调用方决定（<see cref="Save"/> 可直接落盘）。
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    private readonly IConfigStore _store;
    private readonly IShortcutService? _shortcuts;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="store">底层配置读写门面，<see cref="Data"/> 与 <see cref="Save"/> 都委托给它。</param>
    /// <param name="shortcuts">快捷方式服务，用于改名/删除时同步；可为 null（如命令行模式无需快捷方式）。</param>
    public ProfileStore(IConfigStore store, IShortcutService? shortcuts)
    {
        _store = store;
        _shortcuts = shortcuts;
    }

    /// <inheritdoc />
    public ConfigFile Data => _store.Data;

    /// <inheritdoc />
    public Profile? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var want = name.Trim();
        var profiles = Data.Profiles;

        // 一轮：名称完全相等
        foreach (var item in profiles)
        {
            if (string.Equals(item.Name, want, StringComparison.Ordinal)) return item;
        }

        // 二轮：去首尾空格后完全相等
        foreach (var item in profiles)
        {
            if (string.Equals(item.Name.Trim(), want, StringComparison.Ordinal)) return item;
        }

        // 三轮：忽略大小写
        foreach (var item in profiles)
        {
            if (string.Equals(item.Name.Trim(), want, StringComparison.OrdinalIgnoreCase)) return item;
        }

        return null;
    }

    /// <inheritdoc />
    public List<string> GetNameList()
    {
        var names = new List<string>(Data.Profiles.Count);
        foreach (var item in Data.Profiles)
        {
            names.Add(item.Name);
        }
        return names;
    }

    /// <inheritdoc />
    public Profile Add(Profile profile)
    {
        profile.Name = MakeUniqueName(string.IsNullOrWhiteSpace(profile.Name) ? "新配置集" : profile.Name);
        Data.Profiles.Add(profile);
        return profile;
    }

    /// <inheritdoc />
    public Profile Duplicate(string sourceName)
    {
        var source = FindByName(sourceName);
        var sourceDisplayName = source?.Name ?? sourceName;

        var copy = new Profile
        {
            // 旧版命名规则：<原名> - 副本；被占用则 <原名> - 副本2、副本3……
            Name = MakeDuplicateName(sourceDisplayName),
            Remark = source?.Remark ?? "",
            // 深拷贝：各层列表与 AdapterConfig 全部新建实例，不与源共用引用
            Adapters = CloneAdapters(source?.Adapters),
        };

        Data.Profiles.Add(copy);
        return copy;
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        var target = FindByName(name);
        if (target is null) return false;

        var removedName = target.Name;
        Data.Profiles.Remove(target);

        // 删除配置集时一并清掉它的桌面快捷方式
        try
        {
            _shortcuts?.RemoveProfileShortcut(removedName);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除配置集快捷方式失败（{removedName}）：{ex.Message}");
        }

        return true;
    }

    /// <inheritdoc />
    public bool Move(string name, int delta)
    {
        if (delta == 0) return false;

        var target = FindByName(name);
        if (target is null) return false;

        var current = Data.Profiles.IndexOf(target);
        var destination = current + delta;
        if (destination < 0 || destination >= Data.Profiles.Count) return false;

        Data.Profiles.RemoveAt(current);
        Data.Profiles.Insert(destination, target);
        return true;
    }

    /// <inheritdoc />
    public void Save() => _store.Save();

    /// <summary>改名后同步桌面快捷方式（旧名 .lnk 存在才处理）。</summary>
    /// <param name="oldName">原配置集名。</param>
    /// <param name="newName">新配置集名。</param>
    public void SyncShortcutOnRename(string oldName, string newName)
    {
        try
        {
            _shortcuts?.SyncOnRename(oldName, newName);
        }
        catch (Exception ex)
        {
            Log.Warn($"同步桌面快捷方式失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 新增时的去重命名：名称被占用则依次尝试「<c>原名 (2)</c>」「<c>原名 (3)</c>」……
    /// 与旧版快捷方式命名冲突处理无关，这里对应任务契约里「Add 重名自动加 (n) 后缀」的要求。
    /// </summary>
    private string MakeUniqueName(string baseName)
    {
        var candidate = baseName;
        var n = 2;
        while (FindByName(candidate) is not null)
        {
            candidate = $"{baseName} ({n})";
            n++;
        }
        return candidate;
    }

    /// <summary>
    /// 复制时的去重命名：旧版从「 - 副本」起，被占用则依次「 - 副本2」「 - 副本3」……
    /// 注意这里沿用旧版的<b>无空格</b>后缀，不要与 <see cref="MakeUniqueName"/> 的「 (n)」混用。
    /// </summary>
    private string MakeDuplicateName(string sourceName)
    {
        var candidate = $"{sourceName} - 副本";
        var n = 2;
        while (FindByName(candidate) is not null)
        {
            candidate = $"{sourceName} - 副本{n}";
            n++;
        }
        return candidate;
    }

    private static List<AdapterConfig> CloneAdapters(List<AdapterConfig>? source)
    {
        var result = new List<AdapterConfig>();
        if (source is null) return result;

        foreach (var item in source)
        {
            result.Add(new AdapterConfig
            {
                Adapter = item.Adapter,
                Enabled = item.Enabled,
                Mode = item.Mode,
                Ip = item.Ip,
                Mask = item.Mask,
                DhcpDns = item.DhcpDns,
                DefaultMetric = item.DefaultMetric,
                Gateways = item.Gateways is null ? null : new List<string>(item.Gateways),
                Dns = item.Dns is null ? null : new List<string>(item.Dns),
                Routes = item.Routes is null ? null : new List<string>(item.Routes),
                DhcpStaticCoexist = item.DhcpStaticCoexist,
                Ipv6Enabled = item.Ipv6Enabled,
            });
        }
        return result;
    }
}

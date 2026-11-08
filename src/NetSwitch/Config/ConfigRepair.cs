using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 反序列化后的字段兜底修复 —— 照搬旧版 <c>Repair-ConfigData</c> 的全部规则。
///
/// 存在的意义：profiles.json 允许手工编辑，字段可能缺失、类型可能不对、
/// 旧备份里没有新增字段（如 defaultMetric）。这里一次性补齐，让后续代码无需到处判空。
///
/// 规则清单（与旧版逐条对应）：
///   1. Version 缺失补 1；Profiles 缺失补空。
///   2. Profile 的 Name/Remark 缺失补空串；<b>Name 为空则丢弃该 profile</b>。
///   3. Adapter 各字段缺失补默认值；DefaultMetric 经规范化（0-9999，否则 null）。
///   4. Gateways/Dns/Routes 经「去空去空白」规范为列表；null 视为空列表。
///   5. Mode 缺省推导：有 Ip → static，否则 → dhcp；统一小写；非 dhcp/static 回落 dhcp。
///   6. 静态且 Ip 非空时：DhcpDns =（Dns 数量为 0）；Mask 为空补 255.255.255.0。
/// </summary>
public static class ConfigRepair
{
    private const string DefaultMask = "255.255.255.0";

    /// <summary>就地修复配置数据，返回同一实例（便于链式调用）。</summary>
    public static ConfigFile Repair(ConfigFile? data)
    {
        data ??= NewDefaultData();

        if (data.Version <= 0) data.Version = 1;
        data.UpdatedAt ??= "";
        data.Profiles ??= new List<Profile>();

        var kept = new List<Profile>(data.Profiles.Count);
        foreach (var profile in data.Profiles)
        {
            if (profile is null) continue;

            profile.Name ??= "";
            profile.Remark ??= "";
            profile.Adapters ??= new List<AdapterConfig>();

            // 名称为空无法作为唯一键，直接丢弃（与旧版一致）
            if (string.IsNullOrWhiteSpace(profile.Name)) continue;

            var adapters = new List<AdapterConfig>(profile.Adapters.Count);
            foreach (var adapter in profile.Adapters)
            {
                if (adapter is null) continue;
                RepairAdapter(adapter);
                adapters.Add(adapter);
            }

            profile.Adapters = adapters;
            kept.Add(profile);
        }

        data.Profiles = kept;
        return data;
    }

    private static void RepairAdapter(AdapterConfig adapter)
    {
        adapter.Adapter ??= "";
        adapter.Mode ??= "";
        adapter.Ip ??= "";
        adapter.Mask ??= "";

        adapter.DefaultMetric = NormalizeMetric(adapter.DefaultMetric);

        adapter.Gateways = NormalizeStringList(adapter.Gateways);
        adapter.Dns = NormalizeStringList(adapter.Dns);
        adapter.Routes = NormalizeStringList(adapter.Routes);

        // 简化写法兜底：没写 mode 时，填了 IP 就按静态处理
        if (string.IsNullOrWhiteSpace(adapter.Mode))
        {
            adapter.Mode = string.IsNullOrWhiteSpace(adapter.Ip) ? "dhcp" : "static";
        }

        adapter.Mode = adapter.Mode.ToLowerInvariant();
        if (adapter.Mode is not ("dhcp" or "static"))
        {
            adapter.Mode = "dhcp";
        }

        if (adapter.Mode == "static" && !string.IsNullOrWhiteSpace(adapter.Ip))
        {
            adapter.DhcpDns = adapter.Dns.Count == 0;
            if (string.IsNullOrWhiteSpace(adapter.Mask)) adapter.Mask = DefaultMask;
        }
    }

    /// <summary>跃点规范化：仅接受 0-9999 的整数，其余一律 null（含旧配置缺字段的情况）。</summary>
    public static int? NormalizeMetric(int? value)
    {
        if (value is null) return null;
        var v = value.Value;
        return v is >= 0 and <= 9999 ? v : null;
    }

    /// <summary>跃点规范化（字符串来源），对应旧版 <c>ConvertTo-MetricValue</c>。</summary>
    public static int? NormalizeMetric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        // 旧版用 ^\d{1,4}$ —— 只认纯数字，不接受正负号或空白
        if (trimmed.Length is 0 or > 4) return null;
        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiDigit(ch)) return null;
        }

        return int.TryParse(trimmed, out var value) && value is >= 0 and <= 9999
            ? value
            : null;
    }

    /// <summary>转成「去空、去首尾空白」的字符串列表；null 视为空列表。</summary>
    public static List<string> NormalizeStringList(List<string>? source)
    {
        var result = new List<string>();
        if (source is null) return result;

        foreach (var item in source)
        {
            var text = item ?? "";
            if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
        }

        return result;
    }

    /// <summary>生成默认配置：内置「DHCP 自动获取」与示例两个配置集。</summary>
    public static ConfigFile NewDefaultData()
    {
        return new ConfigFile
        {
            Version = 1,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Profiles = new List<Profile>
            {
                new()
                {
                    Name = "DHCP 自动获取",
                    Remark = "恢复全部网卡为自动获取",
                    Adapters = new List<AdapterConfig>
                    {
                        new()
                        {
                            Adapter = "*",
                            Enabled = true,
                            Mode = "dhcp",
                            Ip = "",
                            Mask = "",
                            DhcpDns = true,
                            DefaultMetric = null,
                            Gateways = new List<string>(),
                            Dns = new List<string>(),
                            Routes = new List<string>(),
                        },
                    },
                },
                new()
                {
                    Name = "示例：内网 192.168.10.x",
                    Remark = "改成本机环境的地址后即可使用，网关可写 192.168.10.1:10 指定跃点数",
                    Adapters = new List<AdapterConfig>
                    {
                        new()
                        {
                            Adapter = "以太网",
                            Enabled = true,
                            Mode = "static",
                            Ip = "192.168.10.50",
                            Mask = "255.255.255.0",
                            DhcpDns = false,
                            DefaultMetric = null,
                            Gateways = new List<string> { "192.168.10.1" },
                            Dns = new List<string> { "192.168.10.1", "114.114.114.114" },
                            Routes = new List<string>(),
                        },
                    },
                },
            },
        };
    }
}

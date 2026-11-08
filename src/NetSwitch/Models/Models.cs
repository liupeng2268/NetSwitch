using System.Text.Json.Serialization;

namespace NetSwitch.Models;

// ============================================================================
// 数据模型 —— 与 profiles.json 结构 1:1 对应
//
// 字段名通过 JsonPropertyName 精确锁定，保证与 PowerShell 版读写的
// profiles.json 完全互通（PS 用 camelCase，且存档为带 BOM 的 UTF-8）。
//
// 兼容性铁律：
//   1. 新增字段一律可空 + 有默认值，旧文件缺字段不得报错。
//   2. 读取时 gateways/dns/routes 可能是 null，修复层统一转空列表。
//   3. 写出时保持 PS 的行为：空列表写成 null（减少与旧文件的差异）。
// ============================================================================

/// <summary>profiles.json 顶层对象。</summary>
public sealed class ConfigFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>最后保存时间，格式 yyyy-MM-dd HH:mm:ss。</summary>
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();
}

/// <summary>一个配置集。</summary>
public sealed class Profile
{
    /// <summary>配置集名称，唯一键；为空则该条目会被丢弃。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("remark")]
    public string Remark { get; set; } = "";

    [JsonPropertyName("adapters")]
    public List<AdapterConfig> Adapters { get; set; } = new();
}

/// <summary>配置集里针对一块（或一类）网卡的配置。</summary>
public sealed class AdapterConfig
{
    /// <summary>
    /// 网卡匹配值：名称 / 硬件描述 / MAC / InterfaceGuid / "*" 通配。
    /// 用户填名称时，保存阶段会转成 MAC 存储（改名后仍可识别）。
    /// </summary>
    [JsonPropertyName("adapter")]
    public string Adapter { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"dhcp" 或 "static"，恒小写。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "dhcp";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    /// <summary>子网掩码；也接受 0-32 的前缀数字。</summary>
    [JsonPropertyName("mask")]
    public string Mask { get; set; } = "";

    /// <summary>DHCP 模式下是否使用自动 DNS。</summary>
    [JsonPropertyName("dhcpDns")]
    public bool DhcpDns { get; set; } = true;

    /// <summary>默认路由跃点，0-9999；null 表示不指定。</summary>
    [JsonPropertyName("defaultMetric")]
    public int? DefaultMetric { get; set; }

    /// <summary>网关列表，元素可写 "192.168.10.1:10"（冒号后为跃点）。</summary>
    [JsonPropertyName("gateways")]
    public List<string>? Gateways { get; set; }

    /// <summary>DNS 服务器列表。</summary>
    [JsonPropertyName("dns")]
    public List<string>? Dns { get; set; }

    /// <summary>附加静态路由，元素写 "10.0.0.0/8>192.168.10.254"。</summary>
    [JsonPropertyName("routes")]
    public List<string>? Routes { get; set; }
}

/// <summary>applied-routes.json 的一条记录：NetSwitch 自己加过的路由。</summary>
public sealed class RouteRecord
{
    [JsonPropertyName("interfaceIndex")]
    public int InterfaceIndex { get; set; }

    [JsonPropertyName("destinationPrefix")]
    public string DestinationPrefix { get; set; } = "";

    [JsonPropertyName("nextHop")]
    public string NextHop { get; set; } = "";
}

// ============================================================================
// 以下为运行期对象（不落盘）
// ============================================================================

/// <summary>一块网卡的运行期快照，来源为系统网卡枚举。</summary>
public sealed class AdapterInfo
{
    public int InterfaceIndex { get; set; }
    public string Name { get; set; } = "";

    /// <summary>硬件描述，如 "Realtek PCIe GbE Family Controller"。不随改名变化。</summary>
    public string Description { get; set; } = "";

    /// <summary>原始 MAC，形如 "B0-25-AA-6F-36-44"。</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>网卡 GUID，形如 "{...}"。不随改名变化。</summary>
    public string InterfaceGuid { get; set; } = "";

    /// <summary>是否处于 Up（已连接）状态。</summary>
    public bool Up { get; set; }

    /// <summary>链路速率描述，如 "1 Gbps"；未知时为 "-"。</summary>
    public string LinkSpeedText { get; set; } = "-";

    /// <summary>IPv4 是否启用。</summary>
    public bool Ipv4Enabled { get; set; }

    /// <summary>是否由 DHCP 提供地址。</summary>
    public bool DhcpEnabled { get; set; }

    /// <summary>当前 IPv4 地址（可能为空）。</summary>
    public string Ipv4Address { get; set; } = "";

    /// <summary>当前前缀长度（0 表示未知）。</summary>
    public int PrefixLength { get; set; }

    /// <summary>当前默认网关列表。</summary>
    public List<string> Gateways { get; set; } = new();

    /// <summary>当前 DNS 列表。</summary>
    public List<string> DnsServers { get; set; } = new();
}

/// <summary>解析后的网关条目。</summary>
public sealed class GatewayEntry
{
    public string Address { get; set; } = "";

    /// <summary>冒号后显式指定的跃点；null 表示未指定。</summary>
    public int? Metric { get; set; }
}

/// <summary>解析后的附加路由条目。</summary>
public sealed class RouteEntry
{
    /// <summary>目标网段，如 "10.0.0.0/8"。</summary>
    public string Destination { get; set; } = "";

    /// <summary>下一跳，如 "192.168.10.254"。</summary>
    public string NextHop { get; set; } = "";
}

/// <summary>命令行参数承载。字段与 PS 版 param 块一一对应。</summary>
public sealed class AppOptions
{
    /// <summary>-Apply "配置集名"</summary>
    public string? Apply { get; set; }

    /// <summary>-ListProfiles</summary>
    public bool ListProfiles { get; set; }

    /// <summary>-ListAdapters</summary>
    public bool ListAdapters { get; set; }

    /// <summary>-ShowStatus</summary>
    public bool ShowStatus { get; set; }

    /// <summary>-ImportFile 路径</summary>
    public string? ImportFile { get; set; }

    /// <summary>-ExportFile 路径</summary>
    public string? ExportFile { get; set; }

    /// <summary>-Silent</summary>
    public bool Silent { get; set; }

    /// <summary>-ConfigPath 路径</summary>
    public string? ConfigPath { get; set; }

    /// <summary>-Elevated（内部标记，避免循环提权）</summary>
    public bool Elevated { get; set; }

    /// <summary>诊断用：-DpiMode on|off，本次进程临时覆盖 DPI 开关，不落盘。</summary>
    public string? DpiMode { get; set; }

    /// <summary>诊断用：-DumpLayout 路径，导出控件坐标。</summary>
    public string? DumpLayout { get; set; }

    /// <summary>
    /// 诊断用：-ShotDir 目录，界面启动后自动截图并退出。
    /// 仅开发期核对布局使用，不出现在用户帮助里。
    /// </summary>
    public string? ShotDir { get; set; }

    /// <summary>
    /// 诊断用：-DialogsShot 目录，依次打开各对话框并截图后退出。
    /// 仅开发期核对对话框布局使用。
    /// </summary>
    public string? DialogsShotDir { get; set; }

    /// <summary>是否请求了任一 CLI 动作（决定走命令行还是图形界面）。</summary>
    [JsonIgnore]
    public bool HasCliAction =>
        !string.IsNullOrWhiteSpace(Apply) ||
        ListProfiles || ListAdapters || ShowStatus ||
        !string.IsNullOrWhiteSpace(ImportFile) ||
        !string.IsNullOrWhiteSpace(ExportFile);
}

/// <summary>日志级别，对应 PS 版 INFO / OK / WARN / ERROR。</summary>
public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error,
}

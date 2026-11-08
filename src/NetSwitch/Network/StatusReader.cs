using System.Text;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// 当前网络状态文本。逐行复刻旧版 <c>Get-StatusText</c> 的输出格式，
/// 供主界面状态卡片与命令行 <c>-ShowStatus</c> 共用。
///
/// 旧版有两条实现路径（WMI 快路径 / cmdlet 退路），输出格式基本一致。
/// C# 版统一走 <see cref="IAdapterService.GetAdapters"/> —— 它底层已是 CIM，
/// 等价于旧版的快路径，且天然与主界面网卡列表共用同一套虚拟网卡过滤规则
/// （旧版特意在快路径里也调 <c>Test-VirtualAdapter</c>，就是为避免
/// 「列表里没有、状态里却有」的不一致）。
///
/// 输出样例：
/// <code>
/// ■ 以太网   [已连接]  1 Gbps
///     MAC        : B0-25-AA-6F-36-44
///     获取方式   : 静态
///     IPv4       : 192.168.10.50/24  (255.255.255.0)
///     网关       : 192.168.10.1
///     DNS        : 192.168.10.1
/// </code>
/// </summary>
public sealed class StatusReader : IStatusReader
{
    private readonly IAdapterService _adapters;

    public StatusReader(IAdapterService adapters)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    /// <inheritdoc />
    public string GetStatusText()
    {
        var builder = new StringBuilder(1024);

        List<AdapterInfo> list;
        try
        {
            list = _adapters.GetAdapters();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取网络状态失败：{ex.Message}");
            return "读取网络状态失败，请查看日志。\r\n";
        }

        if (list.Count == 0)
        {
            builder.AppendLine("没有找到已启用且已连接网络的网卡。");
            return builder.ToString();
        }

        foreach (var adapter in list)
        {
            // 只显示已连接且已启用 IPv4 的网卡 —— 与旧版的 IPEnabled = True 过滤等价
            if (!adapter.Up) continue;

            var statusText = adapter.Ipv4Enabled || !string.IsNullOrWhiteSpace(adapter.Ipv4Address)
                ? "已连接"
                : "已连接";

            var name = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;

            builder.AppendLine($"■ {name}   [{statusText}]  {adapter.LinkSpeedText}");
            builder.AppendLine($"    MAC        : {FormatMac(adapter.MacAddress)}");
            builder.AppendLine($"    获取方式   : {(adapter.DhcpEnabled ? "DHCP" : "静态")}");

            if (string.IsNullOrWhiteSpace(adapter.Ipv4Address))
            {
                builder.AppendLine("    IPv4       : （无）");
            }
            else
            {
                var prefix = adapter.PrefixLength > 0 ? adapter.PrefixLength : 24;
                var mask = ParameterParser.PrefixToMask(prefix);
                builder.AppendLine($"    IPv4       : {adapter.Ipv4Address}/{prefix}  ({mask})");
            }

            var gateways = FilterIPv4(adapter.Gateways);
            var dns = FilterIPv4(adapter.DnsServers);

            builder.AppendLine($"    网关       : {(gateways.Count > 0 ? string.Join(", ", gateways) : "（无）")}");
            builder.AppendLine($"    DNS        : {(dns.Count > 0 ? string.Join(", ", dns) : "（自动/无）")}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>MAC 统一成连字符大写，与配置集里的存储写法一致（旧版把 WMI 的冒号换成连字符）。</summary>
    private static string FormatMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";

        // 只保留十六进制字符，再每两位插一个连字符
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return mac.Trim();

        var parts = new string[6];
        for (var i = 0; i < 6; i++) parts[i] = hex.Substring(i * 2, 2);
        return string.Join('-', parts);
    }

    /// <summary>只保留 IPv4 形式的地址（旧版用 ^\d+\.\d+\.\d+\.\d+$ 过滤掉 IPv6）。</summary>
    private static List<string> FilterIPv4(IEnumerable<string>? source)
    {
        var result = new List<string>();
        if (source is null) return result;

        foreach (var item in source)
        {
            if (ParameterParser.IsValidIPv4(item)) result.Add(item.Trim());
        }
        return result;
    }

    /// <summary>
    /// WMI 的 <c>NetConnectionStatus</c> 数字码转中文。保留此映射以对应旧版
    /// <c>ConvertFrom-ConnectionStatus</c>；当前 <see cref="AdapterInfo"/> 已带
    /// 布尔 <c>Up</c>，因此状态文本统一显示「已连接」，此方法供后续细化使用。
    /// </summary>
    internal static string ConnectionStatusText(int code) => code switch
    {
        0 => "已断开",
        1 => "正在连接",
        2 => "已连接",
        3 => "正在断开",
        4 => "硬件不存在",
        5 => "已禁用",
        6 => "硬件故障",
        7 => "网线已拔出",
        8 => "正在验证身份",
        9 => "身份验证成功",
        10 => "身份验证失败",
        11 => "地址无效",
        12 => "需要凭据",
        _ => $"状态 {code}",
    };

    /// <summary>bps 转 Gbps / Mbps，与 <c>Get-NetAdapter</c> 的 LinkSpeed 写法一致。</summary>
    internal static string SpeedText(double bps)
    {
        if (bps <= 0) return "";
        return bps >= 1_000_000_000
            ? $"{Math.Round(bps / 1_000_000_000, 1)} Gbps"
            : $"{Math.Round(bps / 1_000_000, 1)} Mbps";
    }
}

using System.Net;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// 网络参数文本解析。逐条对应旧版函数，行为完全一致。
/// 这些是纯函数，不触碰系统，因此可独立测试。
/// </summary>
public static class ParameterParser
{
    /// <summary>
    /// 掩码 → 前缀长度（对应 ConvertTo-PrefixLength）。
    /// 空 → 24；纯数字 0-32 → 直接采用；否则按点分掩码数 1 的个数。
    /// 解析失败 → 记 WARN 并返回 24。
    /// </summary>
    public static int ToPrefixLength(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return 24;

        var text = mask.Trim();

        // 允许直接写前缀数字，如 "24"
        if (text.Length is 1 or 2 && int.TryParse(text, out var numeric)
            && numeric is >= 0 and <= 32)
        {
            return numeric;
        }

        try
        {
            var bytes = IPAddress.Parse(text).GetAddressBytes();
            var prefix = 0;
            foreach (var b in bytes)
            {
                for (var bit = 7; bit >= 0; bit--)
                {
                    prefix += (b >> bit) & 1;
                }
            }
            return prefix;
        }
        catch
        {
            Log.Warn($"子网掩码格式不合法（{mask}），已按 255.255.255.0 处理。");
            return 24;
        }
    }

    /// <summary>前缀长度 → 掩码（对应 ConvertFrom-PrefixToMask）。</summary>
    public static string PrefixToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32) prefixLength = 24;

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var bits = Math.Min(8, Math.Max(0, prefixLength - i * 8));
            bytes[i] = (byte)(bits == 0 ? 0 : 0xFF << (8 - bits));
        }
        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// 按多种分隔符切分列表（对应 Split-ItemList）。
    /// 支持英文逗号/分号、中文逗号/分号、任意空白。
    /// </summary>
    public static List<string> SplitItemList(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var separators = new[] { ',', ';', '，', '；', ' ', '\t', '\r', '\n' };
        foreach (var part in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result;
    }

    /// <summary>
    /// 网关串 → 条目列表（对应 ConvertTo-GatewayEntry）。
    /// 支持 "192.168.1.1" 与 "192.168.1.1:10"（冒号后为跃点，中英文冒号均可）。
    /// </summary>
    public static List<GatewayEntry> ToGatewayEntries(string? text)
    {
        var result = new List<GatewayEntry>();
        foreach (var item in SplitItemList(text))
        {
            var address = item;
            int? metric = null;

            // 匹配 "地址:跃点"，冒号前后允许空白；中英文冒号都接受
            var idx = FindMetricSeparator(item);
            if (idx > 0)
            {
                var metricText = item[(idx + 1)..].Trim();
                if (metricText.Length > 0 && metricText.All(char.IsAsciiDigit))
                {
                    address = item[..idx].Trim();
                    metric = int.Parse(metricText);
                }
            }

            result.Add(new GatewayEntry { Address = address, Metric = metric });
        }
        return result;
    }

    private static int FindMetricSeparator(string item)
    {
        for (var i = item.Length - 1; i > 0; i--)
        {
            if (item[i] is ':' or '：') return i;
        }
        return -1;
    }

    /// <summary>
    /// 附加路由串 → 条目列表（对应 ConvertTo-RouteEntry）。
    /// 写法 "目标网络&gt;下一跳"，"-&gt;" 与 "=&gt;" 会先规范成 "&gt;"。
    /// 格式不对则记 WARN 并忽略该条。
    /// </summary>
    public static List<RouteEntry> ToRouteEntries(string? text)
    {
        var result = new List<RouteEntry>();
        foreach (var item in SplitItemList(text))
        {
            var normalized = item.Replace("->", ">").Replace("=>", ">");
            var idx = normalized.IndexOf('>');
            if (idx > 0 && idx < normalized.Length - 1)
            {
                result.Add(new RouteEntry
                {
                    Destination = normalized[..idx].Trim(),
                    NextHop = normalized[(idx + 1)..].Trim(),
                });
            }
            else
            {
                Log.Warn($"附加路由格式不正确，已忽略：{item}（正确写法 10.0.0.0/8>192.168.10.254）");
            }
        }
        return result;
    }

    /// <summary>把字符串列表去掉空白项，用于把 List&lt;string&gt;? 规范成可安全遍历的列表。</summary>
    public static List<string> NormalizeList(IEnumerable<string?>? source)
    {
        var result = new List<string>();
        if (source is null) return result;

        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item)) result.Add(item.Trim());
        }
        return result;
    }

    /// <summary>
    /// 校验字符串是否为合法 IPv4 地址（不含掩码）。用于冲突探测前的早退。
    /// </summary>
    public static bool IsValidIPv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return IPAddress.TryParse(text.Trim(), out var ip)
               && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }
}

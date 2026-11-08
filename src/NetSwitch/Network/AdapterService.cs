using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// 网卡枚举与匹配。逐条对应旧版 <c>Test-VirtualAdapter</c> / <c>Get-AdapterList</c> /
/// <c>Find-MatchedAdapters</c> / <c>ConvertTo-MacKey</c> / <c>ConvertTo-GuidKey</c> /
/// <c>Get-AdapterDisplayText</c> / <c>ConvertTo-AdapterStorageValue</c>（旧版第 787-959 行）。
///
/// <para>
/// 数据来源：优先 CIM <c>MSFT_NetAdapter</c>（<c>root\StandardCimv2</c>）——这正是
/// 旧版 <c>Get-NetAdapter</c> 的底层实现，因此字段语义一一对应：
/// <c>Name</c> = NetConnectionID、<c>InterfaceDescription</c> = 硬件描述、
/// <c>MacAddress</c> = <c>NetworkAddresses[0]</c> 按两位加连字符、<c>Status</c> 由
/// <c>InterfaceOperationalStatus</c> 等推导、<c>LinkSpeed</c> = <c>TransmitLinkSpeed</c> 自动单位。
/// 地址 / 网关 / DNS / DHCP 状态从 <c>MSFT_NetIPAddress</c>、<c>MSFT_NetRoute</c>、
/// <c>MSFT_NetIPInterface</c>、<c>MSFT_DNSClientServerAddress</c> 各查一次全量、在内存里按
/// <c>InterfaceIndex</c> 配对（与旧版状态查询刻意采用的批量化思路一致）。
/// </para>
/// <para>
/// CIM 不可用时（极少数受限系统）退化为 <see cref="NetworkInterface"/>，
/// 保证「列出网卡」这个基础功能不会整体失效。
/// </para>
/// </summary>
public sealed class AdapterService : IAdapterService
{
    /// <summary>
    /// 是否排除虚拟 / 非物理网卡。默认 <c>true</c>（= 排除），
    /// 对应旧版脚本变量 <c>$script:ExcludeVirtualAdapters</c> 与界面「包含虚拟网卡」勾选框。
    /// </summary>
    public bool ExcludeVirtualAdapters
    {
        get => _excludeVirtualAdapters;
        set
        {
            if (_excludeVirtualAdapters == value) return;
            _excludeVirtualAdapters = value;
            InvalidateCache();
        }
    }

    private bool _excludeVirtualAdapters = true;

    // ── 缓存 ──────────────────────────────────────────────────────────────
    // 旧版每次调用 Get-NetAdapter 都重新枚举、并重复输出「已跳过 N 块虚拟网卡」。
    // C# 版在同一个 `-ListProfiles` 里会为每个配置集调一次 FindMatched，
    // 导致同一条日志刷 4 遍。加一层短时缓存把它压掉，同时省掉重复的 CIM 查询。
    private List<AdapterProbe>? _probeCache;
    private DateTime _probeCacheAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    /// <summary>「已跳过虚拟网卡」是否已提示过，避免 CLI 里逐配置集重复刷屏。</summary>
    private bool _virtualSkipLogged;

    /// <summary>丢弃缓存。切换网络或用户改勾选后调用，保证下次读到最新状态。</summary>
    public void InvalidateCache()
    {
        _probeCache = null;
        _probeCacheAt = default;
        _virtualSkipLogged = false;
    }

    /// <summary>
    /// 取网卡探针（带短时缓存）。CIM 失败时退化为 <see cref="NetworkInterface"/>。
    /// 缓存只在 3 秒内有效，兼顾「一次 CLI 内不重复刷日志」与「状态变化能及时反映」。
    /// </summary>
    private List<AdapterProbe> GetProbes()
    {
        if (_probeCache is not null && DateTime.UtcNow - _probeCacheAt < CacheTtl)
        {
            return _probeCache;
        }

        List<AdapterProbe> probes;
        try
        {
            probes = BuildFromCim();
        }
        catch (Exception ex)
        {
            Log.Warn($"通过 CIM 读取网卡列表失败，已改用系统网卡接口读取：{ex.Message}");
            try
            {
                probes = BuildFromNetworkInterface();
            }
            catch (Exception ex2)
            {
                Log.Error($"读取网卡列表失败：{ex2.Message}");
                return new List<AdapterProbe>();
            }
        }

        probes.Sort((a, b) => a.Info.InterfaceIndex.CompareTo(b.Info.InterfaceIndex));

        _probeCache = probes;
        _probeCacheAt = DateTime.UtcNow;
        return probes;
    }

    /// <summary>取全部物理网卡，按 InterfaceIndex 升序。</summary>
    public List<AdapterInfo> GetAdapters()
    {
        var probes = GetProbes();

        var all = probes.Select(p => p.Info).ToList();

        // 安全第一：默认把虚拟 / 非物理网卡挡在门外，避免 adapter:"*" 这类通配配置误伤它们
        if (!ExcludeVirtualAdapters) return all;

        var kept = new List<AdapterInfo>(all.Count);
        var skipped = new List<string>();
        foreach (var probe in probes)
        {
            if (VirtualAdapterFilter.IsVirtual(probe.Info.Name, probe.Info.Description, probe.VirtualFlag))
            {
                skipped.Add(probe.Info.Name);
            }
            else
            {
                kept.Add(probe.Info);
            }
        }

        if (skipped.Count > 0 && !_virtualSkipLogged)
        {
            // 同一个进程内只提示一次：CLI 的 -ListProfiles 会为每个配置集调一次 FindMatched，
            // 逐次输出会把真正的结果淹掉。旧版没这问题是因为它每次运行只查一次网卡。
            _virtualSkipLogged = true;
            Log.Info($"已跳过 {skipped.Count} 块虚拟/非物理网卡（{string.Join('、', skipped)}）。" +
                     "如需把它们也纳入管理，请在主界面勾选「包含虚拟网卡」，或在配置文件中关闭该过滤。");
        }

        return kept;
    }

    /// <summary>三级匹配：名称精确 → 名称模糊 → 硬件描述 / MAC / InterfaceGuid。</summary>
    public List<AdapterInfo> FindMatched(string pattern)
    {
        var all = GetAdapters();
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return all;

        // 第一级：网卡名称精确匹配（最常见的正常路径，不打日志，避免刷新/预览时刷屏）
        var exact = all.Where(a => a.Name == pattern).ToList();
        if (exact.Count > 0) return exact;

        // 第二级：网卡名称模糊匹配（同样是正常路径，不打日志）
        var fuzzyPattern = "*" + pattern + "*";
        var fuzzy = all.Where(a => PsWildcard.IsMatch(a.Name, fuzzyPattern)).ToList();
        if (fuzzy.Count > 0) return fuzzy;

        // 第三级：按稳定标识匹配（硬件描述 / MAC / InterfaceGuid）。
        // 走到这里说明网卡很可能被改过名，值得记一条日志说明命中的依据。
        var macKey = ToMacKey(pattern);
        var guidKey = ToGuidKey(pattern);

        // 硬件描述：用户往往只记得「Realtek」「Intel」这类片段，所以做模糊匹配
        var byDesc = all.Where(a => PsWildcard.IsMatch(a.Description, fuzzyPattern)).ToList();
        if (byDesc.Count > 0)
        {
            Log.Info($"网卡「{pattern}」按硬件描述匹配到 {byDesc.Count} 块网卡（{JoinNames(byDesc)}）。");
            return byDesc;
        }

        // MAC：不适合模糊匹配，只在归一化后完全相等、且长度足够时才认定命中
        if (macKey.Length >= 6)
        {
            var byMac = all.Where(a => ToMacKey(a.MacAddress) == macKey).ToList();
            if (byMac.Count > 0)
            {
                Log.Info($"网卡「{pattern}」按 MAC 地址匹配到 {byMac.Count} 块网卡（{JoinNames(byMac)}）。");
                return byMac;
            }
        }

        // GUID：支持完全相等或前缀匹配（配置里可能只填了前一小段）
        if (guidKey.Length >= 8)
        {
            var byGuid = new List<AdapterInfo>();
            foreach (var candidate in all)
            {
                var key = ToGuidKey(candidate.InterfaceGuid);
                if (key.Length < 8) continue;
                if (key == guidKey
                    || key.StartsWith(guidKey, StringComparison.Ordinal)
                    || guidKey.StartsWith(key, StringComparison.Ordinal))
                {
                    byGuid.Add(candidate);
                }
            }
            if (byGuid.Count > 0)
            {
                Log.Info($"网卡「{pattern}」按 InterfaceGuid 匹配到 {byGuid.Count} 块网卡（{JoinNames(byGuid)}）。");
                return byGuid;
            }
        }

        Log.Warn($"未找到匹配「{pattern}」的网卡，已跳过该项配置。" +
                 "网卡匹配字段可以填网卡名称（如「以太网」）、硬件描述（如 Realtek PCIe GbE Family Controller）" +
                 "或 MAC 地址（如 B0-25-AA-6F-36-44）；填硬件描述或 MAC 时，以后在系统里给网卡改名也不会失效。");
        return new List<AdapterInfo>();
    }

    /// <summary>MAC 归一化：只留十六进制大写字符。</summary>
    public string ToMacKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var upper = value.ToUpperInvariant();
        var chars = new char[upper.Length];
        var count = 0;
        foreach (var ch in upper)
        {
            if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F')) chars[count++] = ch;
        }
        return new string(chars, 0, count);
    }

    /// <summary>GUID 归一化：去大括号与空白、转小写。</summary>
    public string ToGuidKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Trim().Replace("{", "").Replace("}", "").Trim().ToLowerInvariant();
    }

    /// <summary>显示用：MAC / GUID → 当前网卡名；查不到或非标识则原样返回。</summary>
    public string GetDisplayText(string? storageValue)
    {
        if (string.IsNullOrWhiteSpace(storageValue)) return "";
        var text = storageValue.Trim();

        // 只需翻译 MAC 形态与 GUID 形态；普通名称和通配符原样返回，避免多余开销
        var macKey = ToMacKey(text);
        var isMac = macKey.Length >= 6 && Regex.IsMatch(text, "^[0-9A-Fa-f:.\\-]+$");
        var isGuid = Regex.IsMatch(text, "^\\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-");
        if (!isMac && !isGuid) return text;

        foreach (var adapter in GetAdapters())
        {
            if (isMac && ToMacKey(adapter.MacAddress) == macKey) return adapter.Name;
            if (isGuid && ToGuidKey(adapter.InterfaceGuid) == ToGuidKey(text)) return adapter.Name;
        }
        return text;
    }

    /// <summary>存储用：仅当唯一匹配一块网卡时把名称转成 MAC；通配符与无匹配原样返回。</summary>
    public string ToStorageValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Trim();

        // 含通配符的一律不转换
        if (value == "*" || value.Contains('*') || value.Contains('?')) return value;

        var matched = FindMatched(value);
        if (matched.Count != 1) return value;

        var mac = matched[0].MacAddress;
        if (string.IsNullOrWhiteSpace(mac)) return value;
        return mac;
    }

    // ── 内部：数据来源 ────────────────────────────────────────────────────

    private static string JoinNames(IEnumerable<AdapterInfo> adapters)
        => string.Join('、', adapters.Select(a => a.Name));

    /// <summary>首选路径：CIM MSFT_NetAdapter + 地址 / 路由 / DNS / DHCP 合并。</summary>
    private static List<AdapterProbe> BuildFromCim()
    {
        using var session = Cim.CreateSession();

        var result = new List<AdapterProbe>();

        var adapters = Cim.Query(session, Cim.StandardCimv2, "SELECT * FROM MSFT_NetAdapter");
        foreach (var instance in adapters)
        {
            var index = Cim.ToInt(Cim.Get(instance, "InterfaceIndex"));
            if (index <= 0) continue;

            var info = new AdapterInfo
            {
                InterfaceIndex = index,
                Name = Cim.ToStr(Cim.Get(instance, "Name")),
                Description = Cim.ToStr(Cim.Get(instance, "InterfaceDescription")),
                MacAddress = Cim.FormatMac(Cim.Get(instance, "NetworkAddresses")),
                InterfaceGuid = Cim.ToStr(Cim.Get(instance, "InterfaceGuid")),
                Up = Cim.ToInt(Cim.Get(instance, "InterfaceOperationalStatus")) == 1,
                LinkSpeedText = Cim.FormatLinkSpeed(Cim.ToULong(Cim.Get(instance, "TransmitLinkSpeed"))),
            };

            result.Add(new AdapterProbe(info, Cim.ToBool(Cim.Get(instance, "Virtual"))));
        }

        // 地址 / 网关 / DNS / DHCP：各查一次全量，再按 InterfaceIndex 配对
        var ipv4ByIndex = new Dictionary<int, List<CimInstance>>();
        foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                     "SELECT * FROM MSFT_NetIPAddress WHERE AddressFamily = 2"))
        {
            var index = Cim.ToInt(Cim.Get(instance, "InterfaceIndex"));
            if (!ipv4ByIndex.TryGetValue(index, out var list))
            {
                list = new List<CimInstance>();
                ipv4ByIndex[index] = list;
            }
            list.Add(instance);
        }

        var dhcpByIndex = new Dictionary<int, bool>();
        var ipv4Enabled = new HashSet<int>();
        foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                     "SELECT * FROM MSFT_NetIPInterface WHERE AddressFamily = 2"))
        {
            var index = Cim.ToInt(Cim.Get(instance, "InterfaceIndex"));
            ipv4Enabled.Add(index);
            dhcpByIndex[index] = Cim.ToInt(Cim.Get(instance, "Dhcp")) == 1;
        }

        var gatewaysByIndex = new Dictionary<int, List<string>>();
        foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                     "SELECT * FROM MSFT_NetRoute WHERE AddressFamily = 2 AND DestinationPrefix = '0.0.0.0/0'"))
        {
            var index = Cim.ToInt(Cim.Get(instance, "InterfaceIndex"));
            if (!gatewaysByIndex.TryGetValue(index, out var list))
            {
                list = new List<string>();
                gatewaysByIndex[index] = list;
            }
            list.Add(Cim.ToStr(Cim.Get(instance, "NextHop")));
        }

        var dnsByIndex = new Dictionary<int, List<string>>();
        foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                     "SELECT * FROM MSFT_DNSClientServerAddress WHERE AddressFamily = 2"))
        {
            var index = Cim.ToInt(Cim.Get(instance, "InterfaceIndex"));
            var list = new List<string>();
            if (Cim.Get(instance, "ServerAddresses") is IEnumerable<string> servers)
            {
                foreach (var server in servers)
                {
                    if (!string.IsNullOrWhiteSpace(server) && !list.Contains(server)) list.Add(server);
                }
            }
            dnsByIndex[index] = list;
        }

        foreach (var probe in result)
        {
            var info = probe.Info;
            var index = info.InterfaceIndex;
            info.Ipv4Enabled = ipv4Enabled.Contains(index);
            info.DhcpEnabled = dhcpByIndex.TryGetValue(index, out var dhcp) && dhcp;
            info.Gateways = gatewaysByIndex.TryGetValue(index, out var gw) ? gw : new List<string>();
            info.DnsServers = dnsByIndex.TryGetValue(index, out var dns) ? dns : new List<string>();

            if (ipv4ByIndex.TryGetValue(index, out var addresses))
            {
                foreach (var address in addresses)
                {
                    var ip = Cim.ToStr(Cim.Get(address, "IPAddress"));
                    if (ip.Length == 0 || ip == "127.0.0.1") continue;
                    info.Ipv4Address = ip;
                    info.PrefixLength = Cim.ToInt(Cim.Get(address, "PrefixLength"));
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>退路：CIM 不可用时用 <see cref="NetworkInterface"/> 读出基础信息。</summary>
    private static List<AdapterProbe> BuildFromNetworkInterface()
    {
        var result = new List<AdapterProbe>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var index = 0;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
            }
            catch
            {
                // 未启用 IPv4 的网卡取不到索引，退化为 0（旧版 Get-NetAdapter 仍会列出，索引由系统给出）
            }
            if (index <= 0) continue;

            ulong speed = 0;
            try
            {
                if (nic.Speed > 0) speed = (ulong)nic.Speed;
            }
            catch
            {
                // 某些虚拟网卡读取速率会抛异常
            }

            var info = new AdapterInfo
            {
                InterfaceIndex = index,
                Name = nic.Name,
                Description = nic.Description,
                MacAddress = FormatMacFromBytes(nic.GetPhysicalAddress()),
                InterfaceGuid = nic.Id,
                Up = nic.OperationalStatus == OperationalStatus.Up,
                LinkSpeedText = speed > 0 ? Cim.FormatLinkSpeed(speed) : "-",
                Ipv4Enabled = true,
            };

            try
            {
                var props = nic.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.ToString();
                    if (ip == "127.0.0.1") continue;
                    info.Ipv4Address = ip;
                    info.PrefixLength = unicast.PrefixLength;
                    break;
                }
                foreach (var gateway in props.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    info.Gateways.Add(gateway.Address.ToString());
                }
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var text = dns.ToString();
                    if (!info.DnsServers.Contains(text)) info.DnsServers.Add(text);
                }
            }
            catch
            {
                // 属性读取失败不影响列出网卡
            }

            result.Add(new AdapterProbe(info, null));
        }

        return result;
    }

    /// <summary>把 <see cref="PhysicalAddress"/> 转成旧版那种 <c>B0-25-AA-6F-36-44</c> 写法。</summary>
    private static string FormatMacFromBytes(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return "";
        return string.Join('-', bytes.Select(b => b.ToString("X2")));
    }
}

/// <summary>
/// 一块网卡的数据 + 数据源自带的 Virtual 标记。
/// 拆出来是因为 <see cref="AdapterInfo"/> 是 P0 已落定的模型，不能加字段。
/// <see cref="VirtualFlag"/> 为 <c>null</c> 表示该数据源没有此属性（旧版对这种情况只走模式匹配）。
/// </summary>
internal sealed class AdapterProbe
{
    internal AdapterProbe(AdapterInfo info, bool? virtualFlag)
    {
        Info = info;
        VirtualFlag = virtualFlag;
    }

    internal AdapterInfo Info { get; }

    internal bool? VirtualFlag { get; }
}

/// <summary>
/// 虚拟 / 非物理网卡判定。对应旧版 <c>Test-VirtualAdapter</c>（第 787-809 行）。
/// 模式列表逐字照抄旧版第 80-94 行，匹配大小写不敏感，同时匹配「网卡名称」与「硬件描述」。
/// </summary>
internal static class VirtualAdapterFilter
{
    /// <summary>旧版 <c>$script:VirtualAdapterPatterns</c> 的逐字副本。</summary>
    internal static readonly string[] Patterns =
    {
        "*VMware*",        // VMware Workstation 虚拟网卡
        "*VMnet*",         // VMnet1 / VMnet8 等，名称里不含 VMware 的情况
        "*VirtualBox*",    // VirtualBox Host-Only / Bridged
        "*Hyper-V*",       // Hyper-V 虚拟交换机
        "*HyperV*",
        "*Bluetooth*",     // 蓝牙网络连接
        "*蓝牙*",
        "*Loopback*",      // 环回适配器
        "*环回*",
        "*本地连接*",      // 手机热点 / 网络共享产生的「本地连接* 」
        "*Teredo*",        // IPv6 隧道伪接口
        "*isatap*",
        "*WAN Miniport*",  // 广域网微型端口，非真实物理网卡
    };

    /// <summary>
    /// 判定一块网卡是否为虚拟 / 非物理网卡。
    /// </summary>
    /// <param name="name">网卡名称。</param>
    /// <param name="description">硬件描述。</param>
    /// <param name="virtualFlag">
    /// 网卡对象自带的 Virtual 标记（部分系统/版本会暴露该属性，旧版优先采信）。
    /// 传 <c>null</c> 表示该数据源没有这个属性（如 WMI 的 Win32_NetworkAdapterConfiguration 探针）。
    /// </param>
    internal static bool IsVirtual(string? name, string? description, bool? virtualFlag)
    {
        if (virtualFlag == true) return true;

        foreach (var pattern in Patterns)
        {
            if (!string.IsNullOrEmpty(name) && PsWildcard.IsMatch(name, pattern)) return true;
            if (!string.IsNullOrEmpty(description) && PsWildcard.IsMatch(description, pattern)) return true;
        }
        return false;
    }
}

/// <summary>
/// PowerShell <c>-like</c> 通配匹配的最小实现。
/// 支持 <c>*</c>（任意长度）、<c>?</c>（单个字符）、<c>[abc]</c> / <c>[a-z]</c> / <c>[!abc]</c> 字符集，
/// 大小写不敏感 —— 与旧版 <c>-like</c> 在网卡名 / 描述 / 模式串上的用法一致。
/// 之所以不直接用 <see cref="Regex"/>：用户填写的匹配串里若出现 <c>[</c> 也会被 PS 当作通配语义。
/// </summary>
internal static class PsWildcard
{
    internal static bool IsMatch(string? input, string? pattern)
    {
        input ??= "";
        pattern ??= "";
        return Match(input, 0, pattern, 0);
    }

    private static bool Match(string s, int si, string p, int pi)
    {
        while (pi < p.Length)
        {
            var pc = p[pi];

            if (pc == '*')
            {
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi == p.Length) return true;
                for (var k = si; k <= s.Length; k++)
                {
                    if (Match(s, k, p, pi)) return true;
                }
                return false;
            }

            if (si >= s.Length) return false;

            if (pc == '?')
            {
                si++;
                pi++;
                continue;
            }

            if (pc == '[')
            {
                var end = p.IndexOf(']', pi + 1);
                if (end < 0)
                {
                    // 没有闭合方括号，按普通字符处理
                    if (!CharEq(s[si], '[')) return false;
                    si++;
                    pi++;
                    continue;
                }

                var negate = pi + 1 < p.Length && p[pi + 1] == '!';
                var start = negate ? pi + 2 : pi + 1;
                var hit = false;
                for (var j = start; j < end; j++)
                {
                    if (j + 2 < end && p[j + 1] == '-')
                    {
                        if (CharInRange(s[si], p[j], p[j + 2])) hit = true;
                        j += 2;
                    }
                    else if (CharEq(s[si], p[j]))
                    {
                        hit = true;
                    }
                }
                if (hit == negate) return false;
                si++;
                pi = end + 1;
                continue;
            }

            if (!CharEq(s[si], pc)) return false;
            si++;
            pi++;
        }

        return si == s.Length;
    }

    private static bool CharEq(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static bool CharInRange(char c, char lo, char hi)
    {
        var v = char.ToUpperInvariant(c);
        var a = char.ToUpperInvariant(lo);
        var b = char.ToUpperInvariant(hi);
        return v >= a && v <= b;
    }
}

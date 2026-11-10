using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Generic;
using Microsoft.Management.Infrastructure.Options;
using NetSwitch.Infrastructure;

namespace NetSwitch.Network;

/// <summary>
/// 网络 API 的「失败原因」附加通道。
///
/// <para>
/// <see cref="INetworkApi"/> 的写方法只返回 <c>bool</c>（P0 已落定，不改），但旧版
/// <c>New-NetRoute ... -ErrorAction Stop</c> 失败时会把异常消息拼进日志
/// （「  附加路由添加失败 X -> Y：{原因}」）。为保持日志文案完整，
/// 由具体实现额外实现本接口，调用方按「是否实现」探测，仍只依赖接口、不依赖具体类型。
/// </para>
/// </summary>
public interface INetworkApiDiagnostics
{
    /// <summary>最近一次写操作失败的原因；成功或未发生时为空串。</summary>
    string LastError { get; }
}

/// <summary>
/// 网络配置读写底层，基于 CIM（<c>root\StandardCimv2</c>）实现，逐条对应旧版
/// <c>Reset-InterfaceIPv4</c> / <c>Set-AdapterConfig</c> 里的系统调用（旧版第 1168-1301 行）。
///
/// <para>
/// 为什么用 CIM 而不是 PowerShell：本机加载 <c>NetTCPIP</c> 模块会失败，
/// <c>System.Management.Automation</c> 不可用；而 <c>MSFT_NetIPAddress</c> / <c>MSFT_NetRoute</c> /
/// <c>MSFT_NetIPInterface</c> 正是 <c>New-NetIPAddress</c> / <c>New-NetRoute</c> /
/// <c>Set-NetIPInterface</c> 的底层 CIM 类，<c>Create</c> 方法与 cmdlet 参数一一对应。
/// </para>
/// <para>
/// 所有 CIM 调用都包了 try/catch，失败只记 <see cref="Log"/>，绝不把异常抛到上层 ——
/// 与旧版「单块网卡失败不中断其余网卡」的容错策略一致。
/// </para>
/// </summary>
public sealed class CimNetworkApi : INetworkApi, INetworkApiDiagnostics
{
    private string _lastError = "";

    /// <inheritdoc />
    public string LastError => _lastError;

    /// <summary>
    /// 附加路由记录。<b>属性注入</b>（而非构造参数）：旧版 <c>Reset-InterfaceIPv4</c> 会先调
    /// <c>Clear-AppliedRoutes</c>，与 <see cref="RouteRegistry"/> 互为依赖，构造注入会形成循环。
    /// 为 null 时跳过清理附加路由（其余功能不受影响）。
    /// </summary>
    public IRouteRegistry? Routes { get; set; }

    // ── 常量：CIM 枚举值（取自 NetTCPIP / DnsClient 的 CDXML 定义） ────────

    private const ushort AddressFamilyIPv4 = 2;
    private const byte DhcpDisabled = 0;
    private const byte DhcpEnabled = 1;
    private const byte CoexistDisabled = 0;          // MSFT_NetIPInterface.DhcpStaticIpCoexistence
    private const byte CoexistEnabled = 1;
    private const ushort RouteProtocolNetMgmt = 3;   // NetRoute.Protocol.NetMgmt
    private const byte PublishNo = 0;                // NetRoute.Publish.No

    /// <summary>清空网卡 IPv4：先清理自加路由记录，再删 IPv4 地址与 0.0.0.0/0 默认路由。</summary>
    public void ResetInterfaceIPv4(int interfaceIndex)
    {
        // 先按记录清掉 NetSwitch 上次为这块网卡加的附加路由：本函数只清 IPv4 地址和默认路由，
        // 配置集里删掉的那条路由会一直残留在系统路由表里，只有靠这份记录才能删干净
        try
        {
            Routes?.Clear(interfaceIndex);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理附加路由记录失败：{ex.Message}");
        }

        try
        {
            using var session = Cim.CreateSession();

            foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                         $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}"))
            {
                var ip = Cim.ToStr(Cim.Get(instance, "IPAddress"));
                if (ip == "127.0.0.1") continue;
                try
                {
                    session.DeleteInstance(Cim.StandardCimv2, instance);
                }
                catch (Exception ex)
                {
                    // 旧版用 -ErrorAction SilentlyContinue：删不掉（如已被系统移除）不算失败
                    Log.Info($"  删除地址 {ip} 失败（可能已不存在）：{ex.Message}");
                }
            }

            foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                         $"SELECT * FROM MSFT_NetRoute WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4} AND DestinationPrefix = '0.0.0.0/0'"))
            {
                try
                {
                    session.DeleteInstance(Cim.StandardCimv2, instance);
                }
                catch (Exception ex)
                {
                    Log.Info($"  删除默认路由失败（可能已不存在）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清空网卡 #{interfaceIndex} 的 IPv4 配置失败：{ex.Message}");
        }
    }

    /// <summary>启用/停用 DHCP。</summary>
    public void SetDhcp(int interfaceIndex, bool enabled)
    {
        try
        {
            using var session = Cim.CreateSession();
            var instance = Cim.QuerySingle(session, Cim.StandardCimv2,
                $"SELECT * FROM MSFT_NetIPInterface WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}");

            if (instance is null)
            {
                Log.Warn($"设置网卡 #{interfaceIndex} 的 DHCP 状态失败：找不到对应的 IPv4 接口。");
                return;
            }

            var options = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Dhcp", (byte)(enabled ? DhcpEnabled : DhcpDisabled),
                    CimType.UInt8, CimFlags.Parameter),
            };
            session.InvokeMethod(Cim.StandardCimv2, instance, "ModifyInstance", options);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置网卡 #{interfaceIndex} 的 DHCP 状态失败：{ex.Message}");
        }
    }

    /// <summary>设置静态地址。成功返回 true。</summary>
    public bool SetStaticAddress(int interfaceIndex, string ipAddress, int prefixLength, string? defaultGateway)
    {
        try
        {
            using var session = Cim.CreateSession();
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("InterfaceIndex", (uint)interfaceIndex, CimType.UInt32, CimFlags.Parameter),
                CimMethodParameter.Create("IPAddress", ipAddress, CimType.String, CimFlags.Parameter),
                CimMethodParameter.Create("PrefixLength", (byte)prefixLength, CimType.UInt8, CimFlags.Parameter),
                CimMethodParameter.Create("AddressFamily", AddressFamilyIPv4, CimType.UInt16, CimFlags.Parameter),
            };
            if (!string.IsNullOrWhiteSpace(defaultGateway))
            {
                // 与 New-NetIPAddress -DefaultGateway 等价：建地址时一并建默认路由
                parameters.Add(CimMethodParameter.Create("DefaultGateway", defaultGateway, CimType.String, CimFlags.Parameter));
            }

            var result = session.InvokeMethod(Cim.StandardCimv2, "MSFT_NetIPAddress", "Create", parameters);
            var errorCode = Cim.ToUInt(result?.ReturnValue);
            if (errorCode is not (0 or null))
            {
                Log.Warn($"写入静态 IP {ipAddress} 失败，错误码 {errorCode}。");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"写入静态 IP {ipAddress} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>新建默认路由（0.0.0.0/0）并指定跃点。</summary>
    public bool AddDefaultRoute(int interfaceIndex, string nextHop, int metric)
    {
        return CreateRoute(interfaceIndex, "0.0.0.0/0", nextHop, metric, quiet: true);
    }

    /// <summary>新建附加静态路由。</summary>
    public bool AddRoute(int interfaceIndex, string destinationPrefix, string nextHop)
    {
        return CreateRoute(interfaceIndex, destinationPrefix, nextHop, null, quiet: false);
    }
    /// <summary>删除指定路由（尽力而为，不存在不算失败）。</summary>
    public bool RemoveRoute(string destinationPrefix, string nextHop)
    {
        try
        {
            using var session = Cim.CreateSession();
            var filter = $"SELECT * FROM MSFT_NetRoute WHERE AddressFamily = {AddressFamilyIPv4} " +
                         $"AND DestinationPrefix = '{Cim.Escape(destinationPrefix)}' AND NextHop = '{Cim.Escape(nextHop)}'";

            var removed = false;
            foreach (var instance in Cim.Query(session, Cim.StandardCimv2, filter))
            {
                try
                {
                    session.DeleteInstance(Cim.StandardCimv2, instance);
                    removed = true;
                }
                catch (Exception ex)
                {
                    Log.Info($"  删除路由 {destinationPrefix} -> {nextHop} 失败（可能已不存在）：{ex.Message}");
                }
            }
            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn($"删除路由 {destinationPrefix} -> {nextHop} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>设置 DNS 服务器列表；<paramref name="reset"/> 为 true 表示恢复自动获取。</summary>
    public void SetDns(int interfaceIndex, List<string> servers, bool reset)
    {
        // 实现选择：走 CIM MSFT_DnsClientServerAddress 的 ModifyInstance（与 Set-DnsClientServerAddress
        // 的底层一致）。相比 netsh，CIM 不依赖外部进程输出解析、没有控制台编码问题。
        // netsh 保留为 CIM 失败时的兜底。
        var addresses = reset ? new List<string>() : ParameterParser.NormalizeList(servers);

        try
        {
            using var session = Cim.CreateSession();
            var instance = Cim.QuerySingle(session, Cim.StandardCimv2,
                $"SELECT * FROM MSFT_DNSClientServerAddress WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}");

            if (instance is null)
            {
                throw new InvalidOperationException("找不到对应的 DNS 客户端配置项。");
            }

            var options = new CimMethodParametersCollection();
            if (reset)
            {
                // 与 -ResetServerAddresses 等价：把源改回 DHCP
                options.Add(CimMethodParameter.Create("ResetServerAddresses", true, CimType.Boolean, CimFlags.Parameter));
            }
            else
            {
                options.Add(CimMethodParameter.Create("ServerAddresses", addresses.ToArray(), CimType.StringArray, CimFlags.Parameter));
            }

            session.InvokeMethod(Cim.StandardCimv2, instance, "ModifyInstance", options);
        }
        catch (Exception ex)
        {
            Log.Info($"  CIM 设置 DNS 失败（{ex.Message}），改用 netsh。");
            SetDnsViaNetsh(interfaceIndex, addresses, reset);
        }
    }

    /// <summary>读取网卡当前 IPv4 地址（可能有多个，取第一个非 127 的）。</summary>
    public string GetPrimaryIPv4Address(int interfaceIndex)
    {
        try
        {
            using var session = Cim.CreateSession();
            foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                         $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}"))
            {
                var ip = Cim.ToStr(Cim.Get(instance, "IPAddress"));
                if (ip.Length > 0 && ip != "127.0.0.1") return ip;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取网卡 #{interfaceIndex} 的 IPv4 地址失败：{ex.Message}");
        }
        return "";
    }

    /// <summary>轮询等待 DHCP 地址落位（排除 127.* 与 169.254.*）。超时返回空串。</summary>
    public string WaitDhcpAddress(int interfaceIndex, int timeoutSeconds)
    {
        // 旧版用 (Get-Date) 比较：Get-Date 每次都会重新求值，这里换成 Stopwatch 语义一致
        var deadline = Environment.TickCount64 + timeoutSeconds * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(400);
            try
            {
                using var session = Cim.CreateSession();
                foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                             $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}"))
                {
                    var ip = Cim.ToStr(Cim.Get(instance, "IPAddress"));
                    if (ip.Length == 0) continue;
                    // 排除 127.* 与 169.254.*（APIPA：DHCP 没拿到地址时系统临时分配的占位地址）
                    if (ip == "127.0.0.1" || ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return ip;
                }
            }
            catch
            {
                // 网卡正在重置的瞬间读取会报错，下一轮再试
            }
        }
        return "";
    }

    /// <summary>请求 DHCP 续租（后台发起，不等待返回）。</summary>
    public void RenewDhcp(string adapterName)
    {
        // 旧版注释：ipconfig /renew 需要网卡名称；不给它加 -Wait：续租失败时 ipconfig 最长要
        // 几十秒才返回，会把界面整个卡死。因此这里只发起、不等待，也不持进程句柄。
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = $"/renew \"{adapterName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Info($"  发起 DHCP 续租失败（不影响配置下发）：{ex.Message}");
        }
    }

    /// <summary>尽力而为的 IP 冲突探测（arp -d + ping + arp -a）。命中返回 true。</summary>
    public bool TestIpConflict(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return false;
        var ip = ipAddress.Trim();
        // 旧版用 ^\d{1,3}(\.\d{1,3}){3}$ 做粗筛，不要求每段合法
        if (!Regex.IsMatch(ip, "^\\d{1,3}(\\.\\d{1,3}){3}$")) return false;

        // 先删掉该地址的 ARP 缓存项
        try
        {
            RunHidden("arp.exe", $"-d {ip}");
        }
        catch
        {
            // arp -d 失败不影响后续判断
        }

        // 发一次短超时 ping 逼出 ARP 请求
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(ip, 800);
            if (reply is not null && reply.Status == IPStatus.Success) return true;
        }
        catch
        {
            // ping 不可达属正常，继续查 ARP 表
        }

        // ping 不通也别急着下结论：不少主机禁 ICMP，但 ARP 仍会应答
        try
        {
            var output = RunHidden("arp.exe", $"-a {ip}");
            var pattern = Regex.Escape(ip);
            var macPattern = new Regex("([0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2}");
            foreach (var line in output.Split('\n'))
            {
                if (Regex.IsMatch(line, pattern) && macPattern.IsMatch(line)) return true;
            }
        }
        catch
        {
            // 读 ARP 表失败视为未探测到
        }

        return false;
    }

    /// <summary>
    /// 开启/关闭 DHCP 与静态 IP 共存。CIM 类型按 <see cref="SetDhcp"/> 的 UInt8 范式推断：
    /// DhcpStaticIpCoexistence 是 0/1 枚举值，与 Dhcp 一样用 ModifyInstance 写入。
    /// 失败只记 WARN，不抛到上层、不阻断主流程。
    /// </summary>
    public void SetDhcpStaticIpCoexistence(int interfaceIndex, bool enabled)
    {
        try
        {
            using var session = Cim.CreateSession();
            var instance = Cim.QuerySingle(session, Cim.StandardCimv2,
                $"SELECT * FROM MSFT_NetIPInterface WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}");

            if (instance is null)
            {
                Log.Warn($"设置网卡 #{interfaceIndex} 的 DHCP/静态共存失败：找不到对应的 IPv4 接口。");
                return;
            }

            var options = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("DhcpStaticIpCoexistence",
                    enabled ? CoexistEnabled : CoexistDisabled, CimType.UInt8, CimFlags.Parameter),
            };
            session.InvokeMethod(Cim.StandardCimv2, instance, "ModifyInstance", options);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置网卡 #{interfaceIndex} 的 DHCP/静态共存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启用/禁用网卡的 IPv6 协议绑定（ms_tcpip6），等价 Enable-/Disable-NetAdapterBinding。
    /// 失败只记 WARN，不抛到上层、不阻断主流程。
    /// </summary>
    public void SetIpv6Binding(int interfaceIndex, bool enabled)
    {
        try
        {
            using var session = Cim.CreateSession();

            // MSFT_NetAdapterBinding 在部分系统上不直接暴露 InterfaceIndex，
            // 先由 MSFT_NetAdapter 把索引换成网卡名，再按 Name + ComponentID 精确定位。
            var adapter = Cim.QuerySingle(session, Cim.StandardCimv2,
                $"SELECT * FROM MSFT_NetAdapter WHERE InterfaceIndex = {interfaceIndex}");
            var name = adapter is null ? "" : Cim.ToStr(Cim.Get(adapter, "Name"));

            var filter = name.Length > 0
                ? $"SELECT * FROM MSFT_NetAdapterBinding WHERE Name = '{Cim.Escape(name)}' AND ComponentID = 'ms_tcpip6'"
                : $"SELECT * FROM MSFT_NetAdapterBinding WHERE InterfaceIndex = {interfaceIndex} AND ComponentID = 'ms_tcpip6'";
            var binding = Cim.QuerySingle(session, Cim.StandardCimv2, filter);

            if (binding is null)
            {
                Log.Warn($"设置网卡 #{interfaceIndex} 的 IPv6 绑定失败：找不到 ms_tcpip6 绑定项。");
                return;
            }

            // Enable() / Disable() 无入参，等价于 Enable-/Disable-NetAdapterBinding
            session.InvokeMethod(Cim.StandardCimv2, binding, enabled ? "Enable" : "Disable",
                new CimMethodParametersCollection());
        }
        catch (Exception ex)
        {
            Log.Warn($"设置网卡 #{interfaceIndex} 的 IPv6 绑定失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读取手动配置的静态 IPv4 地址与前缀，供 DHCP/静态共存切换前暂存。
    /// 失败返回空列表，不抛到上层。
    /// </summary>
    public List<(string Address, int PrefixLength)> GetStaticIPv4Addresses(int interfaceIndex)
    {
        var result = new List<(string Address, int PrefixLength)>();
        try
        {
            using var session = Cim.CreateSession();
            foreach (var instance in Cim.Query(session, Cim.StandardCimv2,
                         $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex = {interfaceIndex} AND AddressFamily = {AddressFamilyIPv4}"))
            {
                var ip = Cim.ToStr(Cim.Get(instance, "IPAddress"));
                if (ip.Length == 0 || ip == "127.0.0.1") continue;
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                // PrefixOrigin：1=Manual（手工静态）、3=Dhcp。只保留静态地址；
                // 属性读不到（0=Other）时按宽松策略保留，避免旧系统漏掉用户手配的地址。
                var origin = Cim.ToInt(Cim.Get(instance, "PrefixOrigin"));
                if (origin == 3) continue;

                var prefix = Cim.ToInt(Cim.Get(instance, "PrefixLength"));
                result.Add((ip, prefix));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取网卡 #{interfaceIndex} 的静态 IPv4 地址失败：{ex.Message}");
        }
        return result;
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────

    /// <summary>建路由的公共实现，等价于 <c>New-NetRoute</c>。</summary>
    private bool CreateRoute(int interfaceIndex, string destinationPrefix, string nextHop, int? metric, bool quiet)
    {
        _lastError = "";
        try
        {
            using var session = Cim.CreateSession();
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("InterfaceIndex", (uint)interfaceIndex, CimType.UInt32, CimFlags.Parameter),
                CimMethodParameter.Create("DestinationPrefix", destinationPrefix, CimType.String, CimFlags.Parameter),
                CimMethodParameter.Create("NextHop", nextHop, CimType.String, CimFlags.Parameter),
                CimMethodParameter.Create("AddressFamily", AddressFamilyIPv4, CimType.UInt16, CimFlags.Parameter),
                CimMethodParameter.Create("Protocol", RouteProtocolNetMgmt, CimType.UInt16, CimFlags.Parameter),
                CimMethodParameter.Create("Publish", PublishNo, CimType.UInt8, CimFlags.Parameter),
            };
            if (metric.HasValue)
            {
                parameters.Add(CimMethodParameter.Create("RouteMetric", (ushort)metric.Value, CimType.UInt16, CimFlags.Parameter));
            }

            var result = session.InvokeMethod(Cim.StandardCimv2, "MSFT_NetRoute", "Create", parameters);
            var errorCode = Cim.ToUInt(result?.ReturnValue);
            if (errorCode is not (0 or null))
            {
                _lastError = Cim.DescribeCimError(errorCode.Value);
                if (!quiet) Log.Warn($"创建路由 {destinationPrefix} -> {nextHop} 失败，错误码 {errorCode}。");
                else Log.Info($"  创建默认路由 {nextHop} 失败：{_lastError}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            if (!quiet) Log.Warn($"创建路由 {destinationPrefix} -> {nextHop} 失败：{ex.Message}");
            else Log.Info($"  创建默认路由 {nextHop} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>DNS 兜底：<c>netsh interface ipv4 set dnsservers</c>（本机实测可用）。</summary>
    private void SetDnsViaNetsh(int interfaceIndex, List<string> servers, bool reset)
    {
        try
        {
            if (reset || servers.Count == 0)
            {
                RunHidden("netsh", $"interface ipv4 set dnsservers name={interfaceIndex} source=dhcp");
                return;
            }

            RunHidden("netsh", $"interface ipv4 set dnsservers name={interfaceIndex} source=static address={servers[0]} register=primary validate=no");
            for (var i = 1; i < servers.Count; i++)
            {
                RunHidden("netsh", $"interface ipv4 add dnsservers name={interfaceIndex} address={servers[i]} index={i + 1} validate=no");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"设置网卡 #{interfaceIndex} 的 DNS 失败：{ex.Message}");
        }
    }

    /// <summary>静默运行一个控制台程序并同步取回输出（用于 arp / netsh）。</summary>
    private static string RunHidden(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process is null) return "";

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}

/// <summary>
/// CIM 访问的公共封装：会话创建、查询、属性读取与格式化。
/// 集中在此是为了让 <see cref="AdapterService"/> / <see cref="CimNetworkApi"/> /
/// <see cref="StatusReader"/> 共用同一套取值与容错逻辑，避免三处各写一份。
/// </summary>
internal static class Cim
{
    internal const string StandardCimv2 = @"root\StandardCimv2";
    internal const string Wmi = @"root\cimv2";

    /// <summary>创建本机 CIM 会话（<c>null</c> 即本机）。</summary>
    internal static CimSession CreateSession() => CimSession.Create(null);

    /// <summary>执行一条查询，返回全部实例；失败返回空序列（不抛异常）。</summary>
    internal static IEnumerable<CimInstance> Query(CimSession session, string ns, string query)
    {
        try
        {
            return session.QueryInstances(ns, "WQL", query).ToList();
        }
        catch
        {
            return Array.Empty<CimInstance>();
        }
    }

    /// <summary>执行一条查询，返回第一条实例；无结果或失败返回 <c>null</c>。</summary>
    internal static CimInstance? QuerySingle(CimSession session, string ns, string query)
    {
        foreach (var instance in Query(session, ns, query))
        {
            return instance;
        }
        return null;
    }

    /// <summary>取属性值（不区分大小写的属性查找），不存在返回 <c>null</c>。</summary>
    internal static object? Get(CimInstance instance, string name)
    {
        try
        {
            foreach (var property in instance.CimInstanceProperties)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }
        catch
        {
            // 属性读取异常视为不存在
        }
        return null;
    }

    /// <summary>转字符串；null / 空数组 / 异常都返回空串。</summary>
    internal static string ToStr(object? value)
    {
        if (value is null) return "";
        try
        {
            if (value is string text) return text;
            if (value is string[] array) return array.Length > 0 ? array[0] : "";
            if (value is IEnumerable<string> sequence)
            {
                foreach (var item in sequence) return item ?? "";
                return "";
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        catch
        {
            return "";
        }
    }

    internal static int ToInt(object? value)
    {
        if (value is null) return 0;
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把 CIM 方法返回的错误码转成可空 uint；null 表示无法判定（不阻断流程）。</summary>
    internal static uint? ToUInt(object? value)
    {
        if (value is null) return null;
        try
        {
            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    internal static ulong ToULong(object? value)
    {
        if (value is null) return 0;
        try
        {
            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    internal static bool ToBool(object? value)
    {
        if (value is null) return false;
        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>转义 WQL 字符串字面量里的单引号（用两个单引号表示）。</summary>
    internal static string Escape(string? text) => (text ?? "").Replace("'", "''");

    /// <summary>
    /// 把 CIM 方法的 UInt32 错误码翻译成人话。
    /// 常见值取自 <c>New-NetRoute</c> 的实测反馈；未知码原样带上数字，便于事后检索。
    /// </summary>
    internal static string DescribeCimError(uint code) => code switch
    {
        0 => "成功",
        1 => "请求不受支持",
        2 => "访问被拒绝（需要管理员权限）",
        3 => "参数不正确",
        5 => "找不到指定的对象（网卡或网关不可达）",
        8 => "状态不可用",
        9 => "未知错误",
        21 => "对象已存在",
        87 => "参数不正确（目标网段或下一跳格式有误）",
        1168 => "找不到指定的元素",
        _ => $"系统返回错误码 {code}",
    };

    /// <summary>
    /// 把 <c>NetworkAddresses[0]</c> 格式化成旧版 <c>Get-NetAdapter</c> 的 MAC 写法：
    /// 每两位一组、连字符分隔、大写（如 <c>B0-25-AA-6F-36-44</c>）。
    /// </summary>
    internal static string FormatMac(object? value)
    {
        var raw = ToStr(value);
        return FormatMac(raw);
    }

    /// <summary>把十二位十六进制串格式化成 <c>B0-25-AA-6F-36-44</c>。</summary>
    internal static string FormatMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
        var builder = new StringBuilder(text.Length + 6);
        for (var i = 0; i < text.Length; i += 2)
        {
            if (builder.Length > 0) builder.Append('-');
            builder.Append(text[i]);
            if (i + 1 < text.Length) builder.Append(text[i + 1]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// bps → 旧版 <c>Get-NetAdapter</c> 的 LinkSpeed 写法（<c>Format-LinkSpeed</c>）：
    /// 依次除以 1000 直到小于 1000，最多到 T；保留 1 位小数；为 0 时返回 <c>-</c>（表示未知）。
    /// </summary>
    internal static string FormatLinkSpeed(ulong bps)
    {
        if (bps == 0) return "-";

        string[] postfixes = { "", "K", "M", "G", "T" };
        var value = (decimal)bps;
        var i = 0;
        while (value >= 1000 && i < postfixes.Length - 1)
        {
            value /= 1000;
            i++;
        }
        var rounded = Math.Round(value, 1);
        return rounded.ToString(CultureInfo.InvariantCulture) + " " + postfixes[i] + "bps";
    }
}

/// <summary>
/// <see cref="AdapterInfo.Up"/> 之外的网卡状态文字（给状态卡片用）。
/// 对应旧版 <c>Get-NetAdapter</c> 的 <c>Status</c> 脚本属性（NetAdapter.Types.ps1xml）。
/// </summary>
internal static class AdapterStatusText
{
    /// <summary>
    /// 由 MSFT_NetAdapter 实例推导 Status 文字，规则逐条照抄 NetAdapter.Types.ps1xml：
    /// 先按 <c>InterfaceOperationalStatus</c> 映射，再对 <c>=2</c>（Down）细分。
    /// </summary>
    internal static string Describe(CimInstance? instance)
    {
        if (instance is null) return "未知";

        var operational = Cim.ToInt(Cim.Get(instance, "InterfaceOperationalStatus"));
        var admin = Cim.ToInt(Cim.Get(instance, "InterfaceAdminStatus"));

        string? text = operational switch
        {
            1 => "Up",
            3 => "Testing",
            4 => "Unknown",
            5 => "Dormant",
            6 => "Not Present",
            7 => "Lower Layer Down",
            _ => null,
        };

        if (operational == 2)
        {
            if (admin == 2)
            {
                text = "Disabled";
            }
            else
            {
                var parts = new List<string>();
                if (Cim.ToBool(Cim.Get(instance, "OperationalStatusDownDefaultPortNotAuthenticated")))
                {
                    parts.Add("Not Authenticated");
                }
                if (Cim.ToBool(Cim.Get(instance, "OperationalStatusDownMediaDisconnected")))
                {
                    parts.Add("Disconnected");
                }
                if (Cim.ToBool(Cim.Get(instance, "OperationalStatusDownLowPowerState")))
                {
                    parts.Add("Low Power State");
                }
                text = parts.Count > 0 ? string.Join(", ", parts) : "";
            }
        }

        return string.IsNullOrWhiteSpace(text) ? "Unknown" : text!;
    }
}

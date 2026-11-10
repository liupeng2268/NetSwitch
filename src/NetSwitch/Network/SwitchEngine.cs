using System.Windows.Forms;
using NetSwitch.Config;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// 配置集应用引擎。逐条对应旧版 <c>Invoke-Profile</c> + <c>Set-AdapterConfig</c>
/// （旧版第 1183-1365 行）。
///
/// <para>
/// 执行顺序、日志文案、失败策略全部照旧：
/// 找不到配置集 / 非管理员 → 直接失败；切换前先备份当前网络状态；
/// 单块网卡失败 <c>failed++</c> 但<b>不中断</b>其余网卡；最后 <c>Sleep(500)</c> 等系统落位。
/// </para>
/// </summary>
public sealed class SwitchEngine : ISwitchEngine
{
    private readonly IProfileStore _profiles;
    private readonly IAdapterService _adapters;
    private readonly INetworkApi _network;
    private readonly IRouteRegistry _routes;
    private readonly IBackupService _backups;

    public SwitchEngine(
        IProfileStore profiles,
        IAdapterService adapters,
        INetworkApi network,
        IRouteRegistry routes,
        IBackupService backups)
    {
        _profiles = profiles;
        _adapters = adapters;
        _network = network;
        _routes = routes;
        _backups = backups;
    }

    /// <summary>应用配置集；<paramref name="dryRun"/> 为 true 时只输出预览，不修改系统。</summary>
    public bool Apply(string profileName, bool dryRun = false)
    {
        var target = _profiles.FindByName(profileName);
        if (target is null)
        {
            var names = _profiles.GetNameList();
            var hint = names.Count > 0
                ? "现有配置集：" + string.Join('、', names)
                : "当前没有任何配置集。";
            Log.Error($"找不到配置集「{profileName}」。（{hint}）");
            return false;
        }

        Log.Info($"开始应用配置集「{target.Name}」");

        if (dryRun)
        {
            foreach (var config in target.Adapters)
            {
                Log.Info($"  [预览] 网卡匹配={config.Adapter}  模式={config.Mode}  IP={config.Ip}");
            }
            return true;
        }

        if (!Elevation.IsAdmin())
        {
            Log.Error("当前没有管理员权限，无法修改网络配置。请以管理员身份运行。");
            return false;
        }

        // 切换会先清空网卡现有地址，所以动手之前先把当前网络状态备份下来
        try
        {
            _backups.SaveCurrentNetwork();
        }
        catch (Exception ex)
        {
            // 备份服务自身已保证「失败只记 WARN」；这里再兜一层，绝不让它影响切换
            Log.Warn($"自动备份失败（不影响本次切换）：{ex.Message}");
        }

        var failed = 0;
        var processed = 0;

        foreach (var config in target.Adapters)
        {
            if (!config.Enabled)
            {
                Log.Info($"网卡匹配「{config.Adapter}」已禁用，跳过。");
                continue;
            }

            var matched = _adapters.FindMatched(config.Adapter);
            if (matched.Count == 0) continue;

            foreach (var adapter in matched)
            {
                processed++;
                try
                {
                    SetAdapterConfig(adapter, config);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error($"网卡「{adapter.Name}」配置失败：{ex.Message}");
                }
            }
        }

        Thread.Sleep(500);

        if (failed == 0)
        {
            Log.Ok($"配置集「{target.Name}」已生效（共处理 {processed} 块网卡）");
            return true;
        }

        Log.Warn($"配置集「{target.Name}」完成，但有 {failed} 项失败（共 {processed} 块网卡）。");
        return false;
    }

    /// <summary>
    /// 配置单块网卡。对应旧版 <c>Set-AdapterConfig</c>（第 1183-1301 行）。
    /// <b>这是 public</b>：备份还原（<c>Restore-BackupToAdapters</c>）也复用同一逻辑，
    /// 旧版同样是直接调 <c>Set-AdapterConfig</c>。
    /// </summary>
    public void SetAdapterConfig(AdapterInfo adapter, AdapterConfig config)
    {
        var index = adapter.InterfaceIndex;
        var name = adapter.Name;
        var isDhcp = config.Mode == "dhcp";
        var dnsList = ParameterParser.NormalizeList(config.Dns);

        Log.Info($"网卡「{name}」(#{index}) -> {(isDhcp ? "DHCP 自动获取" : "静态 IP")}");

        // 功能 3：IPv6 绑定开关。仅 HasValue 时管理；null = 完全不改动现有绑定（不新增任何动作）。
        // 放在 IP 配置之前，与 DHCP / 静态分支无关。
        if (config.Ipv6Enabled.HasValue)
        {
            _network.SetIpv6Binding(index, config.Ipv6Enabled.Value);
        }

        // 功能 2：DHCP 与静态 IP 共存。仅显式 true 时启用；null/false 时下面整段都不执行，
        // 执行路径与改造前完全一致（不新增任何动作）。
        List<(string Address, int PrefixLength)>? preservedStaticIpv4 = null;
        if (config.DhcpStaticCoexist == true)
        {
            // ResetInterfaceIPv4 会清空该网卡全部 IPv4，必须在它之前读出手工静态地址
            preservedStaticIpv4 = _network.GetStaticIPv4Addresses(index);
            _network.SetDhcpStaticIpCoexistence(index, enabled: true);
            Log.Info($"  已开启 DHCP/静态 IP 共存，暂存 {preservedStaticIpv4.Count} 个静态 IPv4 地址。");
            Log.Info("  提示：WaitDhcpAddress 可能把保留的静态 IP 误判为 DHCP 地址，属已知现象，不改变其返回值语义。");
        }

        _network.ResetInterfaceIPv4(index);

        if (isDhcp)
        {
            ApplyDhcp(index, name, config, dnsList);
        }
        else
        {
            if (!ApplyStatic(index, name, config, dnsList)) return;
        }

        // 功能 2：DHCP 启用后把 Reset 前暂存的静态地址写回；失败只 WARN，不阻断主流程。
        if (preservedStaticIpv4 is not null && preservedStaticIpv4.Count > 0)
        {
            RestoreStaticIPv4(index, preservedStaticIpv4);
        }

        ApplyExtraRoutes(index, config);
    }

    /// <summary>
    /// 把 Reset 前暂存的静态 IPv4 地址逐个写回（DHCP/静态共存）。
    /// 新增网络操作：try/catch + WARN，不抛到上层、不阻断主流程，也不做自动回滚。
    /// </summary>
    private void RestoreStaticIPv4(int index, List<(string Address, int PrefixLength)> addresses)
    {
        foreach (var (address, prefix) in addresses)
        {
            try
            {
                var effectivePrefix = prefix > 0 ? prefix : 24;
                if (_network.SetStaticAddress(index, address, effectivePrefix, null))
                {
                    Log.Ok($"  已写回保留的静态 IP {address}/{effectivePrefix}");
                }
                else
                {
                    Log.Warn($"  写回保留的静态 IP {address} 失败（不影响本次切换）。");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"  写回保留的静态 IP {address} 失败（不影响本次切换）：{ex.Message}");
            }
        }
    }

    // ── DHCP 分支 ─────────────────────────────────────────────────────────

    private void ApplyDhcp(int index, string name, AdapterConfig config, List<string> dnsList)
    {
        if (dnsList.Count == 0 || config.DhcpDns)
        {
            _network.SetDns(index, new List<string>(), reset: true);
        }

        _network.SetDhcp(index, enabled: true);
        Thread.Sleep(300);

        // ipconfig /renew 需要网卡名称。这里后台发起续租、随后轮询地址是否落位，
        // 而不是等待返回：续租失败时 ipconfig 最长要几十秒才返回，会把界面整个卡死。
        _network.RenewDhcp(name);
        var leasedIp = _network.WaitDhcpAddress(index, 6);
        if (leasedIp.Length > 0)
        {
            Log.Ok($"  已获取到 DHCP 地址 {leasedIp}");
        }
        else
        {
            Log.Warn("  等待 DHCP 地址超时（6 秒）：网线未插好或网内没有 DHCP 服务器时属正常，配置已下发，可稍后手动执行 ipconfig /renew。");
        }

        if (dnsList.Count > 0 && !config.DhcpDns)
        {
            _network.SetDns(index, dnsList, reset: false);
        }

        var dnsNote = dnsList.Count > 0 && !config.DhcpDns
            ? "DNS 固定为 " + string.Join(", ", dnsList)
            : "DNS 自动";
        Log.Ok($"  已切换为自动获取（{dnsNote}）");
    }

    // ── 静态分支 ──────────────────────────────────────────────────────────

    /// <summary>静态 IP 写入。返回 false 表示按旧版逻辑提前中止（不写附加路由）。</summary>
    private bool ApplyStatic(int index, string name, AdapterConfig config, List<string> dnsList)
    {
        var ipAddress = (config.Ip ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            Log.Warn("  静态模式但未填写 IP 地址，已跳过该网卡。");
            return false;
        }

        // 写入前先探一下这个地址是否已被局域网内其他设备占用。
        // 探测结果不可靠（离线设备、静态 ARP 项都会造成假阳性），所以只 WARN + 让用户选，
        // 绝不自动中止 —— 误拦会让用户切不了网，比误报严重得多。
        if (_network.TestIpConflict(ipAddress))
        {
            Log.Warn($"  探测到 IP {ipAddress} 可能已被局域网内其他设备占用。");

            var proceed = true;
            if (!Log.IsCliMode)
            {
                var answer = MessageBox.Show(
                    $"探测到 IP 地址 {ipAddress} 可能已被局域网内其他设备占用。\r\n\r\n" +
                    "继续写入可能造成 IP 冲突，导致两台机器都上不了网。\r\n\r\n" +
                    "如果确认该地址当前无人使用（对方已关机也可能触发本提示），可以选择「仍然继续」。",
                    "NetSwitch - IP 可能冲突",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                proceed = answer == DialogResult.Yes;
            }

            if (!proceed)
            {
                Log.Warn($"  已按用户选择跳过网卡「{name}」的静态 IP 写入。");
                return false;
            }
            Log.Info("  用户选择仍然继续写入。");
        }

        var prefix = ParameterParser.ToPrefixLength(config.Mask);
        _network.SetDhcp(index, enabled: false);
        Thread.Sleep(150);

        // 网关串可能来自配置（List<string>），拼回一个字符串交给解析器（与旧版 -join ';' 一致）
        var gateways = ParameterParser.ToGatewayEntries(string.Join(';', config.Gateways ?? new List<string>()));

        // 网卡上指定了「默认跃点」时，统一改走「先建地址、再手工建默认路由」这条路，
        // 因为 New-NetIPAddress -DefaultGateway 会强行用系统默认 metric，覆盖不掉。
        var configMetric = ConfigRepair.NormalizeMetric(config.DefaultMetric);

        if (gateways.Count == 1 && gateways[0].Metric is null && configMetric is null)
        {
            _network.SetStaticAddress(index, ipAddress, prefix, gateways[0].Address);
        }
        else
        {
            _network.SetStaticAddress(index, ipAddress, prefix, null);

            var step = 0;
            foreach (var gateway in gateways)
            {
                // 优先级：网关串里写的 1.1.1.1:10  > 网卡的「默认跃点」  > 自动 10+序号
                // 多个网关时即使指定了跃点也要逐个 +1，避免两条默认路由 metric 完全相同
                int metric;
                if (gateway.Metric is not null) metric = gateway.Metric.Value;
                else if (configMetric is not null) metric = configMetric.Value + step;
                else metric = 10 + step;

                // 旧版用 -ErrorAction SilentlyContinue：建不出来不报错、不中断
                _network.AddDefaultRoute(index, gateway.Address, metric);
                step++;
            }
        }

        if (dnsList.Count > 0)
        {
            _network.SetDns(index, dnsList, reset: false);
        }
        else
        {
            _network.SetDns(index, new List<string>(), reset: true);
        }

        var summary = $"  IP={ipAddress}/{prefix}";
        if (gateways.Count > 0)
        {
            summary += "  网关=" + string.Join(',', gateways.Select(g => g.Address));
        }
        if (configMetric is not null)
        {
            summary += $"  默认跃点={configMetric.Value}";
        }
        if (dnsList.Count > 0)
        {
            summary += "  DNS=" + string.Join(',', dnsList);
        }
        Log.Ok(summary);

        return true;
    }

    // ── 附加静态路由（两个分支共用） ──────────────────────────────────────

    private void ApplyExtraRoutes(int index, AdapterConfig config)
    {
        foreach (var routeText in ParameterParser.NormalizeList(config.Routes))
        {
            foreach (var route in ParameterParser.ToRouteEntries(routeText))
            {
                // 先删同名再建（旧版行为：去重）
                _network.RemoveRoute(route.Destination, route.NextHop);

                if (_network.AddRoute(index, route.Destination, route.NextHop))
                {
                    // 记下这条是 NetSwitch 加的，下次切换前先按记录删，配置集里删掉的才不会残留
                    _routes.Register(index, route.Destination, route.NextHop);
                    Log.Ok($"  附加路由 {route.Destination} -> {route.NextHop}");
                }
                else
                {
                    // 旧版这里拼的是异常消息；具体实现通过 INetworkApiDiagnostics 提供失败原因，
                    // 取不到就退化为「写入系统路由表失败」。
                    var reason = (_network as INetworkApiDiagnostics)?.LastError;
                    if (string.IsNullOrWhiteSpace(reason)) reason = "写入系统路由表失败";
                    Log.Warn($"  附加路由添加失败 {route.Destination} -> {route.NextHop}：{reason}");
                }
            }
        }
    }
}

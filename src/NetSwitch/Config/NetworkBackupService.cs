using NetSwitch.Infrastructure;
using NetSwitch.Models;
using NetSwitch.Network;

namespace NetSwitch.Config;

/// <summary>
/// 网络状态备份 —— 对应旧版 <c>Get-BackupFiles</c> / <c>Get-CurrentNetworkProfile</c> /
/// <c>Save-NetworkBackup</c> / <c>Import-BackupAsProfiles</c> / <c>Get-BackupRestoreTargets</c> /
/// <c>Restore-BackupToAdapters</c>（脚本第 485-704 行）。
///
/// 用途：切换配置集之前把「当前实际网络状态」存成一份与 profiles.json 同构的快照，
/// 出错或想反悔时可通过「备份记录」导入后切回，或直接写回网卡。
/// 所有失败只记日志，绝不影响切换主流程。
/// </summary>
public sealed class NetworkBackupService : IBackupService
{
    /// <summary>备份保留份数，超出的旧备份会被清理。</summary>
    private const int BackupKeepCount = 10;

    /// <summary>备份文件名前缀。</summary>
    private const string BackupFilePrefix = "NetSwitch-backup-";

    private readonly IAdapterService _adapters;
    private readonly INetworkApi _network;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="adapters">网卡枚举与匹配服务。</param>
    /// <param name="network">网络配置读写（还原时写网卡）。</param>
    /// <param name="configPath">配置文件路径（决定备份目录位置）。为空则取默认配置路径。</param>
    public NetworkBackupService(IAdapterService adapters, INetworkApi network, string? configPath)
    {
        _adapters = adapters;
        _network = network;

        var effectivePath = string.IsNullOrWhiteSpace(configPath)
            ? Paths.GetDefaultConfigPath()
            : configPath;

        BackupDir = Paths.GetBackupDir(effectivePath);
    }

    /// <inheritdoc />
    public string BackupDir { get; }

    /// <inheritdoc />
    public string SaveCurrentNetwork()
    {
        try
        {
            if (!Directory.Exists(BackupDir))
            {
                Directory.CreateDirectory(BackupDir);
            }

            var data = BuildCurrentNetworkProfile();
            var file = Path.Combine(BackupDir, $"{BackupFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var json = PsJson.Serialize(data);

            // 带 BOM 写入，保证 Windows 记事本打开不乱码
            FileIo.WriteAllTextWithBom(file, json);

            // 裁剪旧备份，保留最新 10 份
            var existing = ListFiles();
            if (existing.Count > BackupKeepCount)
            {
                foreach (var old in existing.Skip(BackupKeepCount))
                {
                    try { File.Delete(old); } catch { /* 删除失败不影响主流程 */ }
                }
            }

            Log.Ok($"已自动备份当前网络状态：{file}");
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn($"自动备份失败（不影响本次切换）：{ex.Message}");
            return "";
        }
    }

    /// <inheritdoc />
    public List<string> ListFiles()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(BackupDir)) return result;

            result = Directory
                .EnumerateFiles(BackupDir, $"{BackupFilePrefix}*.json")
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取备份目录失败：{ex.Message}");
        }
        return result;
    }

    /// <inheritdoc />
    public int ImportAsProfiles(string backupPath, ConfigFile target)
    {
        if (!File.Exists(backupPath))
        {
            Log.Error("备份文件不存在。");
            return 0;
        }

        var raw = PsJson.TryReadAllText(backupPath);
        var parsed = PsJson.TryDeserialize<ConfigFile>(raw);
        if (parsed is null)
        {
            Log.Error($"读取备份失败：备份内容不是合法的 JSON（{backupPath}）。");
            return 0;
        }

        var data = ConfigRepair.Repair(parsed);

        target.Profiles ??= new List<Profile>();

        // 已占用的名称集合（建档时用即时比较，避免大小写/空格差异造成漏判）
        var added = 0;
        foreach (var item in data.Profiles)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name)) continue;

            var baseName = item.Name;
            var name = baseName;
            var n = 1;
            while (NameTaken(target.Profiles, name))
            {
                n++;
                name = $"{baseName} ({n})";
            }

            item.Name = name;
            target.Profiles.Add(item);
            added++;
        }

        return added;
    }

    /// <inheritdoc />
    public bool RestoreToAdapters(string backupPath)
    {
        if (!Elevation.IsAdmin())
        {
            Log.Error("还原备份需要管理员权限，请以管理员身份运行本程序后再试。");
            return false;
        }

        List<RestoreTarget> targets;
        try
        {
            targets = GetRestoreTargets(backupPath);
        }
        catch (Exception ex)
        {
            Log.Error($"读取备份失败：{ex.Message}");
            return false;
        }

        if (targets.Count == 0)
        {
            Log.Warn("备份中没有匹配到本机现有的网卡，未做任何改动。");
            return false;
        }

        Log.Info($"开始还原备份：{backupPath}");

        // 还原同样会先清空网卡地址，动手之前先把当前状态再存一份，方便出错时回退
        SaveCurrentNetwork();

        var failed = 0;
        var processed = 0;

        foreach (var target in targets)
        {
            processed++;
            try
            {
                ApplyConfig(target.Adapter, target.Config);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"网卡「{target.Adapter.Name}」还原失败：{ex.Message}");
            }
        }

        Thread.Sleep(500);

        if (failed == 0)
        {
            Log.Ok($"备份已还原到网卡（共处理 {processed} 块网卡）");
            return true;
        }

        Log.Warn($"备份还原完成，但有 {failed} 项失败（共 {processed} 块网卡）。");
        return false;
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 采集各网卡当前的 IPv4 / 掩码 / 网关 / DNS，组装成与 profiles.json 同构的配置集
    /// （对应旧版 <c>Get-CurrentNetworkProfile</c>）。
    /// 只有网卡是手动静态地址（DhcpEnabled == false）才记 mode="static" 并写 ip/mask；
    /// DHCP 获取到的地址不写死。
    /// </summary>
    private ConfigFile BuildCurrentNetworkProfile()
    {
        var entries = new List<AdapterConfig>();

        foreach (var adapter in _adapters.GetAdapters())
        {
            var entry = new AdapterConfig
            {
                Adapter = adapter.Name,
                Enabled = true,
                Mode = "dhcp",
                Ip = "",
                Mask = "",
                DhcpDns = true,
                DefaultMetric = null,
                Gateways = new List<string>(),
                Dns = new List<string>(),
                Routes = new List<string>(),
            };

            // 只有手动设置了静态地址才记录为 static，DHCP 获取到的地址不写死
            if (!adapter.DhcpEnabled && !string.IsNullOrWhiteSpace(adapter.Ipv4Address))
            {
                entry.Mode = "static";
                entry.Ip = adapter.Ipv4Address;
                entry.Mask = ParameterParser.PrefixToMask(adapter.PrefixLength);
            }

            entry.Gateways = new List<string>(adapter.Gateways);
            entry.Dns = new List<string>(adapter.DnsServers);
            entry.DhcpDns = entry.Dns.Count == 0;

            entries.Add(entry);
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return new ConfigFile
        {
            Version = 1,
            UpdatedAt = stamp,
            Profiles = new List<Profile>
            {
                new()
                {
                    Name = $"自动备份 {stamp}",
                    Remark = "切换配置集之前自动保存的当前网络状态，可通过「备份记录」导入后切回",
                    Adapters = entries,
                },
            },
        };
    }

    /// <summary>
    /// 读出备份文件，按当前机器的网卡做匹配，返回将要被改动的「网卡 + 配置」清单
    /// （对应旧版 <c>Get-BackupRestoreTargets</c>）。
    /// </summary>
    private List<RestoreTarget> GetRestoreTargets(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("备份文件不存在。", backupPath);
        }

        var raw = PsJson.TryReadAllText(backupPath);
        var parsed = PsJson.TryDeserialize<ConfigFile>(raw);
        if (parsed is null)
        {
            throw new InvalidDataException("备份内容不是合法的 JSON。");
        }

        var data = ConfigRepair.Repair(parsed);
        var targets = new List<RestoreTarget>();

        foreach (var profile in data.Profiles)
        {
            if (profile is null) continue;

            foreach (var config in profile.Adapters)
            {
                if (config is null || !config.Enabled) continue;

                foreach (var adapter in _adapters.FindMatched(config.Adapter))
                {
                    targets.Add(new RestoreTarget
                    {
                        Profile = profile.Name,
                        Adapter = adapter,
                        Config = config,
                    });
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// 把一条网卡配置写回系统。这是旧版 <c>Set-AdapterConfig</c> 中与「地址 / 网关 / DNS」
    /// 相关的核心路径（静态与 DHCP 两种模式），metric / 附加路由的完整处理属于
    /// <see cref="ISwitchEngine"/>（网络模块），此处只做还原所需的最小写入。
    /// </summary>
    private void ApplyConfig(AdapterInfo adapter, AdapterConfig config)
    {
        var index = adapter.InterfaceIndex;
        var name = adapter.Name;
        var isDhcp = string.Equals(config.Mode, "dhcp", StringComparison.OrdinalIgnoreCase);
        var dnsList = ParameterParser.NormalizeList(config.Dns);

        Log.Info($"网卡「{name}」(#{index}) -> {(isDhcp ? "DHCP 自动获取" : "静态 IP")}");

        _network.ResetInterfaceIPv4(index);

        if (isDhcp)
        {
            if (dnsList.Count == 0 || config.DhcpDns)
            {
                _network.SetDns(index, new List<string>(), reset: true);
            }

            _network.SetDhcp(index, true);
            Thread.Sleep(300);

            _network.RenewDhcp(name);
            var leasedIp = _network.WaitDhcpAddress(index, 6);
            if (!string.IsNullOrWhiteSpace(leasedIp))
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
            return;
        }

        var ipAddress = (config.Ip ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            Log.Warn("  静态模式但未填写 IP 地址，已跳过该网卡。");
            return;
        }

        var prefix = ParameterParser.ToPrefixLength(config.Mask);
        _network.SetDhcp(index, false);
        Thread.Sleep(150);

        var gateways = ParameterParser.ToGatewayEntries(string.Join(";", config.Gateways ?? new List<string>()));
        var defaultGateway = gateways.Count > 0 ? gateways[0].Address : null;

        _network.SetStaticAddress(index, ipAddress, prefix, defaultGateway);

        // 除首个网关走 SetStaticAddress 的默认网关外，其余网关补建默认路由
        for (var step = 1; step < gateways.Count; step++)
        {
            var metric = gateways[step].Metric ?? 10 + step;
            _network.AddDefaultRoute(index, gateways[step].Address, metric);
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
            summary += "  网关=" + string.Join(",", gateways.Select(g => g.Address));
        }
        if (dnsList.Count > 0)
        {
            summary += "  DNS=" + string.Join(",", dnsList);
        }
        Log.Ok(summary);
    }

    /// <summary>备份内的配置集名是否已被占用（宽松匹配，与 <see cref="ProfileStore.FindByName"/> 口径一致）。</summary>
    private static bool NameTaken(List<Profile> profiles, string name)
    {
        var want = name.Trim();
        foreach (var item in profiles)
        {
            if (item is null) continue;
            if (string.Equals(item.Name, want, StringComparison.Ordinal)) return true;
            if (string.Equals(item.Name.Trim(), want, StringComparison.Ordinal)) return true;
            if (string.Equals(item.Name.Trim(), want, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>一条待还原的「网卡 + 配置」映射（对应旧版 Get-BackupRestoreTargets 的输出项）。</summary>
    private sealed class RestoreTarget
    {
        public string Profile { get; init; } = "";
        public AdapterInfo Adapter { get; init; } = new();
        public AdapterConfig Config { get; init; } = new();
    }
}

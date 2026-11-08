using System.Text.Json;
using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// NetSwitch 自己添加过的附加路由记录（<c>applied-routes.json</c>）。
/// 对应旧版 <c>Get-AppliedRoutesPath</c> / <c>Read-AppliedRoutes</c> / <c>Write-AppliedRoutes</c> /
/// <c>Register-AppliedRoute</c> / <c>Clear-AppliedRoutes</c>（旧版第 969-1094 行）。
///
/// <para>
/// 目的：<c>ResetInterfaceIPv4</c> 只清 IPv4 地址与 <c>0.0.0.0/0</c> 默认路由，清不掉 NetSwitch
/// 上次写入的附加路由，于是「配置集里删掉某条路由、再切一次、系统里那条还在」就成了必然。
/// 解决办法：把自己加过的路由记到文件里，下次切换同一网卡时先按记录删一遍。
/// 存文件而不是只放内存，是因为图形界面切换完通常就退出了，内存记录会随之丢失。
/// </para>
/// <para>
/// 只删自己加过的，绝不碰系统或用户手工添加的路由。
/// </para>
/// <para>
/// 编码规则与 profiles.json <b>相反</b>：本文件是 UTF-8 <b>无</b> BOM（旧版用
/// <c>New-Object System.Text.UTF8Encoding($false)</c>），因此走 <c>FileIo.WriteAllTextNoBom</c>。
/// </para>
/// </summary>
public sealed class RouteRegistry : IRouteRegistry
{
    /// <summary>记录文件绝对路径；定位不到可写位置时为空串（此时读写都退化为空操作）。</summary>
    public string FilePath { get; }

    /// <summary>
    /// 删路由用的网络 API。<b>属性注入</b>（而非构造参数）：
    /// <see cref="CimNetworkApi.ResetInterfaceIPv4"/> 需要回调 <see cref="IRouteRegistry.Clear"/>，
    /// 两者互为依赖。装配顺序示例：
    /// <code>
    /// var routes  = new RouteRegistry(configPath);
    /// var network = new CimNetworkApi { Routes = routes };
    /// routes.Network = network;
    /// </code>
    /// 为 null 时 <see cref="Clear"/> 只写回剩余记录、不实际删除（不抛异常）。
    /// </summary>
    public INetworkApi? Network { get; set; }

    /// <summary>构造：定位记录文件（候选路径取第一个可写的）。</summary>
    public RouteRegistry(string configPath)
    {
        FilePath = ResolvePath(configPath);
    }

    /// <summary>
    /// 依次探测候选路径，取第一个可写的（与旧版 <c>Get-AppliedRoutesPath</c> 的「试探写入」一致）。
    /// </summary>
    private static string ResolvePath(string configPath)
    {
        foreach (var candidate in Paths.GetAppliedRoutesPathCandidates(configPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                // 试探写入，确认这个位置真的可写
                File.AppendAllText(candidate, string.Empty);
                return candidate;
            }
            catch
            {
                // 换下一个候选
            }
        }
        return "";
    }

    /// <summary>读取全部记录（文件损坏视为空）。</summary>
    public List<RouteRecord> Read()
    {
        var result = new List<RouteRecord>();
        if (string.IsNullOrWhiteSpace(FilePath)) return result;

        try
        {
            if (!File.Exists(FilePath)) return result;
            var raw = PsJson.TryReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var items = ParseRecords(raw);
            foreach (var item in items)
            {
                if (item is null) continue;
                result.Add(new RouteRecord
                {
                    InterfaceIndex = item.InterfaceIndex,
                    DestinationPrefix = item.DestinationPrefix ?? "",
                    NextHop = item.NextHop ?? "",
                });
            }
        }
        catch
        {
            // 记录文件坏了就当没记录过，宁可漏删也不能让它影响切换
            return new List<RouteRecord>();
        }

        return result;
    }

    /// <summary>
    /// 解析记录数组。正常是数组；也兼容「单条记录被写成裸对象」的历史/手工文件
    /// （旧版注释里提到的 -InputObject 坑）。
    /// </summary>
    private static List<RouteRecord> ParseRecords(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            var single = JsonSerializer.Deserialize<RouteRecord>(raw);
            return single is null ? new List<RouteRecord>() : new List<RouteRecord> { single };
        }

        return JsonSerializer.Deserialize<List<RouteRecord>>(raw) ?? new List<RouteRecord>();
    }

    /// <summary>写回记录。空记录写 <c>[]</c>，编码 UTF-8 无 BOM。</summary>
    private void Write(List<RouteRecord> records)
    {
        if (string.IsNullOrWhiteSpace(FilePath)) return;

        try
        {
            var json = PsJson.SerializeRoutes(records);
            FileIo.WriteAllTextNoBom(FilePath, json);
        }
        catch
        {
            // 记不下来不影响切换，最坏结果是下次漏清一次
        }
    }

    /// <summary>去重登记一条路由。</summary>
    public void Register(int interfaceIndex, string destinationPrefix, string nextHop)
    {
        var recorded = Read();
        foreach (var route in recorded)
        {
            if (route.InterfaceIndex == interfaceIndex
                && route.DestinationPrefix == destinationPrefix
                && route.NextHop == nextHop)
            {
                return;
            }
        }

        recorded.Add(new RouteRecord
        {
            InterfaceIndex = interfaceIndex,
            DestinationPrefix = destinationPrefix,
            NextHop = nextHop,
        });
        Write(recorded);
    }

    /// <summary>按记录删除附加路由，并写回剩余记录。<paramref name="interfaceIndex"/> 传 0 表示清全部。</summary>
    public void Clear(int interfaceIndex = 0)
    {
        var recorded = Read();
        if (recorded.Count == 0) return;

        var remaining = new List<RouteRecord>();
        foreach (var route in recorded)
        {
            if (interfaceIndex != 0 && route.InterfaceIndex != interfaceIndex)
            {
                remaining.Add(route);
                continue;
            }

            // 旧版：Remove-NetRoute ... -ErrorAction Stop，成功记 OK、失败记 INFO
            // 删不掉通常是它已经被系统或用户移除了；写普通日志即可，用 WARN 会每次切换都刷一条噪音
            var removed = Network?.RemoveRoute(route.DestinationPrefix, route.NextHop) ?? false;
            if (removed)
            {
                Log.Ok($"  清理上次添加的附加路由 {route.DestinationPrefix} -> {route.NextHop}");
            }
            else
            {
                Log.Info($"  附加路由 {route.DestinationPrefix} -> {route.NextHop} 已不存在，跳过清理");
            }
        }

        Write(remaining);
    }
}

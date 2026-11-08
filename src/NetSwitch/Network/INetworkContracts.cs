using NetSwitch.Models;

namespace NetSwitch.Network;

/// <summary>
/// 网卡枚举与匹配。取代旧版的 Get-AdapterList / Test-VirtualAdapter /
/// Find-MatchedAdapters / ConvertTo-MacKey / ConvertTo-GuidKey /
/// Get-AdapterDisplayText / ConvertTo-AdapterStorageValue 七个函数。
/// </summary>
public interface IAdapterService
{
    /// <summary>是否把虚拟/非物理网卡挡在门外（对应界面「包含虚拟网卡」勾选框，默认 true = 排除）。</summary>
    bool ExcludeVirtualAdapters { get; set; }

    /// <summary>
    /// 取全部物理网卡，按 InterfaceIndex 升序。
    /// 首次调用会输出一条「已跳过 N 块虚拟网卡」的 INFO 日志（与旧版行为一致）。
    /// </summary>
    List<AdapterInfo> GetAdapters();

    /// <summary>
    /// 三级匹配：名称精确 → 名称模糊 → 硬件描述 / MAC / InterfaceGuid。
    /// <paramref name="pattern"/> 为空或 "*" 时返回全部网卡。
    /// 走第三级或匹配失败时会写日志（与旧版一致）。
    /// </summary>
    List<AdapterInfo> FindMatched(string pattern);

    /// <summary>MAC 归一化：只留十六进制大写字符。B0-25-AA.. / b0:25:aa.. / B025AA.. 互为同一值。</summary>
    string ToMacKey(string? value);

    /// <summary>GUID 归一化：去大括号与空白、转小写。</summary>
    string ToGuidKey(string? value);

    /// <summary>显示用：MAC / GUID → 当前网卡名；查不到或非标识则原样返回。</summary>
    string GetDisplayText(string? storageValue);

    /// <summary>存储用：仅当唯一匹配一块网卡时把名称转成 MAC；通配符与无匹配原样返回。</summary>
    string ToStorageValue(string? text);
}

/// <summary>
/// 网络配置的读写底层。基于 CIM（root\StandardCimv2），
/// 是 Get-NetAdapter 系列 cmdlet 的底层实现，因此不依赖任何 PowerShell 运行时。
///
/// 需要管理员权限的写操作：<see cref="SetStaticAddress"/> / <see cref="SetDhcp"/> /
/// <see cref="AddDefaultRoute"/> / <see cref="AddRoute"/> / <see cref="RemoveRoute"/>。
/// </summary>
public interface INetworkApi
{
    /// <summary>清空网卡 IPv4：先清理自加路由记录，再删 IPv4 地址与 0.0.0.0/0 默认路由。</summary>
    void ResetInterfaceIPv4(int interfaceIndex);

    /// <summary>启用/停用 DHCP。</summary>
    void SetDhcp(int interfaceIndex, bool enabled);

    /// <summary>设置静态地址。成功返回 true。</summary>
    bool SetStaticAddress(int interfaceIndex, string ipAddress, int prefixLength, string? defaultGateway);

    /// <summary>新建默认路由（0.0.0.0/0）并指定跃点。</summary>
    bool AddDefaultRoute(int interfaceIndex, string nextHop, int metric);

    /// <summary>新建附加静态路由。</summary>
    bool AddRoute(int interfaceIndex, string destinationPrefix, string nextHop);

    /// <summary>删除指定路由（尽力而为，不存在不算失败）。</summary>
    bool RemoveRoute(string destinationPrefix, string nextHop);

    /// <summary>设置 DNS 服务器列表；传空表示恢复自动获取。</summary>
    void SetDns(int interfaceIndex, List<string> servers, bool reset);

    /// <summary>读取网卡当前 IPv4 地址（可能有多个，取第一个非 127 的）。</summary>
    string GetPrimaryIPv4Address(int interfaceIndex);

    /// <summary>轮询等待 DHCP 地址落位（排除 127.* 与 169.254.*）。超时返回空串。</summary>
    string WaitDhcpAddress(int interfaceIndex, int timeoutSeconds);

    /// <summary>请求 DHCP 续租（后台发起，不等待返回）。</summary>
    void RenewDhcp(string adapterName);

    /// <summary>尽力而为的 IP 冲突探测（arp -d + ping + arp -a）。命中返回 true。</summary>
    bool TestIpConflict(string ipAddress);
}

/// <summary>
/// NetSwitch 自己添加过的路由记录（applied-routes.json）。
/// 目的：切换时只清 IPv4 地址与默认路由，清不掉附加路由；
/// 靠这份记录，下次切换前能把上次加的路由删干净，配置集里删掉的才不残留。
/// </summary>
public interface IRouteRegistry
{
    /// <summary>记录文件路径。</summary>
    string FilePath { get; }

    /// <summary>读取全部记录（文件损坏视为空）。</summary>
    List<RouteRecord> Read();

    /// <summary>按记录删除附加路由，并写回剩余记录。interfaceIndex 传 0 表示清全部。</summary>
    void Clear(int interfaceIndex = 0);

    /// <summary>去重登记一条路由。</summary>
    void Register(int interfaceIndex, string destinationPrefix, string nextHop);
}

/// <summary>配置集应用引擎。</summary>
public interface ISwitchEngine
{
    /// <summary>
    /// 应用配置集。<paramref name="dryRun"/> 为 true 时只输出预览，不修改系统。
    /// 返回是否全部成功（部分网卡失败返回 false，但不中断其余网卡）。
    /// </summary>
    bool Apply(string profileName, bool dryRun = false);
}

/// <summary>当前网络状态文本（用于主界面状态卡片与 CLI -ShowStatus）。</summary>
public interface IStatusReader
{
    /// <summary>生成多行状态文本。快路径走 CIM，失败回退 cmdlet 等价实现。</summary>
    string GetStatusText();
}

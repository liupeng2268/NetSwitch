using NetSwitch.Config;
using NetSwitch.Models;
using NetSwitch.Network;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 服务容器 —— 各模块的唯一装配点。
///
/// 设计意图：让四个模块（配置 / 网络 / 界面 / 命令行）**只依赖本类与接口**，
/// 不互相直接引用具体实现类。这样四个模块可并行开发、单独编译、互不阻塞，
/// 装配顺序只在一个地方（Program.cs）显式写出。
///
/// 用属性注入而非完整 DI 容器：本项目服务数量固定、生命周期一致（进程级单例），
/// 引入 DI 框架只会增加理解成本。
/// </summary>
public sealed class AppServices
{
    /// <summary>命令行参数（含 -ConfigPath / -Silent 等）。</summary>
    public required AppOptions Options { get; init; }

    /// <summary>配置文件读写门面。</summary>
    public required IConfigStore Config { get; init; }

    /// <summary>配置集增删改查与排序。</summary>
    public required IProfileStore Profiles { get; init; }

    /// <summary>配置文件自身的备份轮转。</summary>
    public required IConfigBackup ConfigBackup { get; init; }

    /// <summary>网络状态备份（切换前自动保存）。</summary>
    public required IBackupService Backups { get; init; }

    /// <summary>配置导入 / 导出。</summary>
    public required IConfigTransfer Transfer { get; init; }

    /// <summary>网卡枚举、匹配与标识转换。</summary>
    public required IAdapterService Adapters { get; init; }

    /// <summary>网络配置读写（CIM）。</summary>
    public required INetworkApi Network { get; init; }

    /// <summary>自己添加过的路由的记录与清理。</summary>
    public required IRouteRegistry Routes { get; init; }

    /// <summary>配置集应用引擎。</summary>
    public required ISwitchEngine Engine { get; init; }

    /// <summary>当前网络状态文本。</summary>
    public required IStatusReader Status { get; init; }

    /// <summary>桌面快捷方式生成与同步。</summary>
    public required IShortcutService Shortcuts { get; init; }
}

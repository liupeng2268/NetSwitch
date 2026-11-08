using NetSwitch.Infrastructure;
using NetSwitch.Models;

namespace NetSwitch.Cli;

/// <summary>
/// 命令行模式入口。逐条对应旧版 <c>Invoke-Cli</c> 的分支。
///
/// 输出格式必须与旧版一致（除时间戳），因为验证环节要做逐字比对：
///   <code>
///   ── 配置集列表 ─────────────────────────────
///     1. DHCP 自动获取   [恢复全部网卡为自动获取]
///         * → DHCP 自动获取
///   </code>
/// </summary>
public interface ICliRunner
{
    /// <summary>
    /// 执行命令行动作。返回进程退出码（0 成功，1 失败）。
    /// 调用前必须已完成 <c>Log.Initialize</c> 与配置加载。
    /// </summary>
    int Run(AppServices services);
}

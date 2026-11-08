using NetSwitch.Models;

namespace NetSwitch.Config;

/// <summary>
/// 配置集的增删改查顺序与移动。
///
/// 说明：这些操作原本散落在 PS 脚本的 GUI 事件里（$btnNew / $btnCopy / $btnDelete /
/// $moveProfile 等内联块），C# 版把它们提出来放在此处，由界面层调用，
/// 使「改名时同步快捷方式」「删除时清理引用」等副作用有统一落点。
/// 当前处于 P0 骨架阶段，仅定义契约；P1 由「基础与配置」模块补全实现。
/// </summary>
public interface IProfileStore
{
    /// <summary>当前内存中的配置数据。</summary>
    ConfigFile Data { get; }

    /// <summary>按名称查找配置集（宽松匹配：去首尾空格、忽略大小写）。</summary>
    Profile? FindByName(string name);

    /// <summary>全部配置集名称。</summary>
    List<string> GetNameList();

    /// <summary>新增配置集（重名时自动加「 (n)」后缀）。</summary>
    Profile Add(Profile profile);

    /// <summary>复制配置集，命名「&lt;原名&gt; - 副本」。</summary>
    Profile Duplicate(string sourceName);

    /// <summary>删除配置集，返回是否成功。</summary>
    bool Remove(string name);

    /// <summary>上移（delta = -1）或下移（delta = +1）。</summary>
    bool Move(string name, int delta);

    /// <summary>把内存数据写回磁盘（写前自动备份）。</summary>
    void Save();
}

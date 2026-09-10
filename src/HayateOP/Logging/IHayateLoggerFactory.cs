namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// 池级日志工厂（M19）。<br />
/// 用途：为每个对象池按「池名（category）」创建独立日志器，替代全局单例日志；
/// 典型场景为多池共存时把日志按池名分流（如输出到不同的 MEL category / 文件）。
/// </summary>
/// <remarks>
/// 零破坏约定：未通过 <c>HayatePoolBuilder&lt;T&gt;.WithLoggerFactory</c> 指定工厂时，
/// 行为与 2.4 及之前完全一致——沿用 Builder 内建的单例 <see cref="IHayateLogger"/>。
/// 若同时显式调用 <c>WithLogger</c>，则显式实例优先于工厂。
/// </remarks>
public interface IHayateLoggerFactory
{
    /// <summary>
    /// 为指定逻辑池创建日志器。
    /// </summary>
    /// <param name="categoryName">日志分类名（默认为池名 <c>HayatePoolBuilder&lt;T&gt;.WithPoolName</c> 的值，缺省为元素类型名）。</param>
    /// <returns>日志器实例；不可返回 <c>null</c>。</returns>
    IHayateLogger CreateLogger(string categoryName);
}

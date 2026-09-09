namespace DotNetCore.HayateOP;

/// <summary>
/// 管理端点「池列表」返回的单池摘要（M11+，2.5）。
/// 数据源自注册表完整版 <see cref="IHayateObjectPoolRegistry.GetAll"/>。
/// </summary>
/// <param name="PoolName">逻辑池名。</param>
/// <param name="ElementType">池化元素类型全名；非泛型实现为 null。</param>
/// <param name="RegisteredAt">注册（≈构建）时间。</param>
/// <param name="PooledCount">当前池内空闲对象数。</param>
/// <param name="BorrowedCount">当前借出对象数。</param>
public sealed record HayatePoolSummary(
    string PoolName,
    string ElementType,
    System.DateTimeOffset RegisteredAt,
    int PooledCount,
    int BorrowedCount);

namespace DotNetCore.Extensions.ObjectPool;

/// <summary>
/// 标记对象可在归还时自动重置状态
/// </summary>
public interface IResettable
{
    void Reset();
}
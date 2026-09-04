namespace DotNetCore.HayateOP.Scaling;

/// <summary>
/// 自动伸缩策略接口，用于根据当前池的状态和配置选项计算新的池大小。
/// </summary>
public interface IHayateScalingStrategy
{
    /// <summary>
    /// 计算新的池大小
    /// </summary>
    /// <param name="currentSize"></param>
    /// <param name="idleCount"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    int CalculateNewSize(int currentSize, int idleCount, HayatePoolOptions options);
}
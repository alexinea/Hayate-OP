namespace DotNetCore.HayateOP;

/// <summary>
/// 选项配置
/// </summary>
public class ObjectPoolOptions
{
    public int MaxConcurrent { get; set; } = 10;

    public int MaxPoolSize { get; set; } = 20;
}
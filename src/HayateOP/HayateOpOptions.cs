namespace HayateOP;

/// <summary>
/// 选项配置
/// </summary>
public class HayateOpOptions
{
    public int MaxConcurrent { get; set; } = 10;

    public int MaxPoolSize { get; set; } = 20;

    public bool EnableMetrics { get; set; } = false;
}
using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Reflection;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// T15：<see cref="HayateObjectPoolCompatProvider"/> 构建参数。
/// </summary>
public sealed class HayateCompatOptions
{
    /// <summary>生成的池名前缀（后接 <c>typeof(T).Name</c>）。默认 <c>"HayateCompat."</c>。</summary>
    public string PoolNamePrefix { get; set; } = "HayateCompat.";

    /// <summary>
    /// 预热数量。默认 0（MEOP DefaultObjectPool 也是懒创建）；
    /// 若希望规避首次借出的冷启动延迟（见 <see cref="AcquireTimeout"/>），可设为预期并发量。
    /// </summary>
    public int MinSize { get; set; } = 0;

    /// <summary>
    /// 容量上限。默认 <c>Environment.ProcessorCount * 2</c>——
    /// 对齐 MEOP DefaultObjectPool 的默认 <c>maximumRetained</c> 口径。
    /// </summary>
    public int MaxSize { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// 空池借出超时。默认 1s。
    /// ⚠️ HayateOP 当前 CreateNew 语义是「等满该超时后创建」（PR-C 实测 L6，PR-D 改进项），
    /// 因此冷池首次借出的延迟 ≈ 此值——与 MEOP「空池同步立即创建、永不阻塞」存在差异。
    /// 缩短该值可降低冷启动延迟，或用 <see cref="MinSize"/> 预热规避。
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// T15：<see cref="ObjectPoolProvider"/> 的 HayateOP 实现——
/// 用既有 <see cref="IPooledObjectPolicy{T}"/> 构建由 HayateOP 驱动的
/// <see cref="ObjectPool{T}"/>，使 MEOP 调用方零代码改动切换。
/// </summary>
/// <example>
/// <code>
/// // 唯一需要改动的一行：
/// // ObjectPoolProvider provider = new DefaultObjectPoolProvider();
/// ObjectPoolProvider provider = new HayateObjectPoolCompatProvider();
/// var pool = provider.Create&lt;MyPolicy&gt;();   // 之后 Get/Return 与 MEOP 完全一致
/// </code>
/// </example>
public sealed class HayateObjectPoolCompatProvider : ObjectPoolProvider
{
    private readonly HayateCompatOptions _options;

    /// <summary>以默认参数构建。</summary>
    public HayateObjectPoolCompatProvider()
        : this(new HayateCompatOptions())
    {
    }

    /// <summary>以自定义参数构建。</summary>
    public HayateObjectPoolCompatProvider(HayateCompatOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override ObjectPool<T> Create<T>(IPooledObjectPolicy<T> policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        // HayatePoolBuilder<T> 带 new() 约束，而 MEOP 的 IPooledObjectPolicy<T> 只有 class 约束；
        // 经 MakeGenericMethod 分发（CLR 不做 new() 运行时校验，对象创建完全由策略承担）。
        var pool = (IHayateObjectPool<T>)BuildPoolMethod.MakeGenericMethod(typeof(T))
            .Invoke(null, new object?[] { policy, _options })!;

        return new HayateObjectPoolAdapter<T>(pool);
    }

    private static readonly MethodInfo BuildPoolMethod =
        typeof(HayateObjectPoolCompatProvider).GetMethod(nameof(BuildPoolCore),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IHayateObjectPool<TP> BuildPoolCore<TP>(
        IPooledObjectPolicy<TP> policy, HayateCompatOptions options) where TP : class, new()
    {
        return new HayatePoolBuilder<TP>()
            .WithPoolName(options.PoolNamePrefix + typeof(TP).Name)
            .WithMinSize(options.MinSize)
            .WithMaxSize(Math.Max(options.MaxSize, options.MinSize))
            .WithPolicy(new HayateCompatPooledObjectPolicy<TP>(policy))
            // MEOP 语义对齐：空池最终创建而非阻塞抛异常。
            // 注意 CreateNew 在 HayateOP 中是「等满 AcquireTimeout 后创建」（L6）。
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(options.AcquireTimeout)
            // ⚠️ 必须保持 autoScaling 开启：HayatePoolOptions.ApplyFeatureSwitches()
            // 在 EnableAutoScaling=false 时强制 MaxPoolSize = MinPoolSize（:487-490），
            // Min=0 时容量塌缩为 0 → 所有分片 max=0 → 一切归还被拒、池化完全失效。
            // （T11/T12 期间"极简池 Min 必须抬高"的怪象同源于此。）
            // Min=0 保留懒创建语义；扩缩容周期可能回收长期空闲对象（HayateOP 语义），
            // 回收后 Get() 会经历一次 AcquireTimeout 冷启动延迟——见 HayateCompatOptions 注释。
            .WithEnableAutoScaling(true)
            .WithEnableValidation(false)       // 校验/reset 已由 Return 钩子（策略层）承担
            .WithEnableEviction(false)         // MEOP 无后台驱逐
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();
    }
}

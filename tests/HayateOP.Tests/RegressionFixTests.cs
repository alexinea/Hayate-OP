using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// 回归测试：覆盖 HayateOP-next-steps.md 中 T01~T03 三项 bug 修复。
/// </summary>
public class RegressionFixTests
{
    private sealed class TestObject
    {
        public int Id { get; init; }
    }

    /// <summary>
    /// T01：OnRelease 返回 false 时，对象被销毁并从池中移除，OnDestroy 被触发。
    /// 修复前：返回值被丢弃，对象被错误地放回分片队列，下次借出时仍能拿到"坏对象"。
    /// 修复后：当 OnRelease=false 销毁对象且池被掏空，自动扩容维持 MinPoolSize，
    ///       下次 Acquire 能拿到一个全新创建的对象（D1 二次缺陷修复）。
    /// </summary>
    [Fact]
    public void T01_OnRelease_FalseReturned_ObjectDestroyedAndOnDestroyInvoked()
    {
        var policy = new RejectOnReleasePolicy<TestObject>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)        // 预热时创建 1 个对象，保证第一次 Acquire 能成功
            .WithMaxSize(4)
            .WithPolicy(policy)
            .WithEnableMetrics(true)
            .Build();

        // Act
        var obj = pool.Acquire();
        pool.Release(obj);

        // Assert：策略层的 OnRelease 应被调用并返回 false；
        //       OnDestroy 应被调用（销毁路径）。
        // 注：旧版断言要求 PooledCount==0，但 D1 修复（销毁后自动扩容维持水位）
        //     会让池被补回，PooledCount 不再为 0；以 NotSame 替代即可证明补池生效。
        Assert.True(policy.OnReleaseInvoked);
        Assert.True(policy.OnDestroyInvoked);

        // D1 验证：销毁后池应自动扩容维持水位，能成功借出全新对象（非被拒对象）。
        var obj2 = pool.Acquire();
        Assert.NotSame(obj, obj2);
    }

    /// <summary>
    /// T02-A：HayatePoolOptions 增加 ScaleDownStep 字段，默认值为 5，且 IsValid 仍通过。
    /// </summary>
    [Fact]
    public void T02_Options_ScaleDownStep_HasDefaultOfFive()
    {
        var options = new HayatePoolOptions();
        Assert.Equal(5, options.ScaleDownStep);
        Assert.Equal(5, options.ScaleUpStep);
        Assert.True(options.IsValid(), "默认 ScaleDownStep=5 应让 IsValid 校验通过");
    }

    /// <summary>
    /// T02-B：ThresholdScalingStrategy 缩容分支使用 ScaleDownStep 而非误用 ScaleUpStep。
    /// 修复前：缩容 = currentSize - ScaleUpStep（10），结果会被钳制到 MinPoolSize。
    /// </summary>
    [Fact]
    public void T02_ThresholdScalingStrategy_ScaleDown_UsesScaleDownStep()
    {
        var strategy = new ThresholdScalingStrategy();
        var options = new HayatePoolOptions
        {
            MinPoolSize = 1,
            MaxPoolSize = 100,
            ScaleUpStep = 10,
            ScaleDownStep = 3,
            ScaleDownThreshold = 0.5,
        };

        // currentSize=20, idleCount=18, usage=0.1（小于 ScaleDownThreshold 0.5）→ 缩容
        // 期望 newSize = max(20 - 3, MinPoolSize=1) = 17（修复前会得 10，钳制到 1）
        var newSize = strategy.CalculateNewSize(currentSize: 20, idleCount: 18, options: options);
        Assert.Equal(17, newSize);

        // 把 ScaleDownStep 改为 7，期望 13
        options.ScaleDownStep = 7;
        newSize = strategy.CalculateNewSize(currentSize: 20, idleCount: 18, options: options);
        Assert.Equal(13, newSize);
    }

    /// <summary>
    /// T02-C：currentSize &lt;= 0 时不产生 NaN/Infinity，直接返回 currentSize。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void T02_ThresholdScalingStrategy_CurrentSizeNonPositive_NoNaN(int currentSize)
    {
        var strategy = new ThresholdScalingStrategy();
        var options = new HayatePoolOptions
        {
            MinPoolSize = 0,
            MaxPoolSize = 100,
        };

        var newSize = strategy.CalculateNewSize(currentSize: currentSize, idleCount: 0, options: options);
        Assert.Equal(currentSize, newSize);
        Assert.False(double.IsNaN(newSize));
        Assert.False(double.IsInfinity(newSize));
    }

    /// <summary>
    /// T03：构造时 PreWarm 会按 shardCount 均匀分配 _maxSize。
    /// 修复 UpdateShardMaxSizes 索引越界的目的是在动态扩容/缩容路径上保持一致行为；
    /// 由于构造路径 PreWarm 与 UpdateShardMaxSizes 使用同一公式，这里通过反射断言
    /// _shards[i]._maxSize 均为 MaxPoolSize/ShardCount（无越界、无误分配）。
    /// </summary>
    [Fact]
    public void T03_ShardMaxSizes_EvenSplit_Max100Shard4()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithShardCount(4)
            .WithMinSize(1)
            .WithMaxSize(100)
            .Build();

        var poolType = pool.GetType();
        var shardsField = poolType.GetField("_shards",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(shardsField);

        var shards = shardsField.GetValue(pool) as System.Array;
        Assert.NotNull(shards);
        Assert.Equal(4, shards.Length);

        var shardType = shards.GetType().GetElementType();
        var maxSizeField = shardType.GetField("_maxSize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(maxSizeField);

        for (var i = 0; i < shards.Length; i++)
        {
            var max = (int)maxSizeField.GetValue(shards.GetValue(i));
            Assert.Equal(25, max);
        }
    }

    /// <summary>
    /// D2：HayatePoolBuilder.WithScaleDownStep 应能正确写入 options.ScaleDownStep，
    /// 与 WithScaleUpStep 行为对齐；非法值（&lt; 1）抛 ArgumentOutOfRangeException。
    /// </summary>
    [Fact]
    public void D2_Builder_WithScaleDownStep_WritesOption()
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithScaleDownStep(7);

        var optionsField = typeof(HayatePoolBuilder<TestObject>)
            .GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(optionsField);
        var options = (HayatePoolOptions)optionsField.GetValue(builder);
        Assert.Equal(7, options.ScaleDownStep);
        Assert.True(options.IsValid());
    }

    [Fact]
    public void D2_Builder_WithScaleDownStep_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HayatePoolBuilder<TestObject>().WithScaleDownStep(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HayatePoolBuilder<TestObject>().WithScaleDownStep(-1));
    }

    /// <summary>
    /// 测试用策略：OnRelease 始终返回 false（拒绝回池），并暴露 OnDestroy 调用标记。
    /// </summary>
    private sealed class RejectOnReleasePolicy<T> : IHayateObjectPolicy<T> where T : class
    {
        public bool OnReleaseInvoked { get; private set; }
        public bool OnDestroyInvoked { get; private set; }

        public T Create() => (T)Activator.CreateInstance(typeof(T));

        public bool OnRelease(T item)
        {
            OnReleaseInvoked = true;
            return false;
        }

        public bool Validate(T item) => true;

        public void OnAcquire(T item) { }

        public void OnPassivate(T item) { }

        public void OnDestroy(T item) => OnDestroyInvoked = true;
    }
}
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Regression tests covering the three bug fixes documented in HayateOP-next-steps.md.
/// </summary>
public class RegressionFixTests
{
    private sealed class TestObject
    {
        public int Id { get; init; }
    }

    /// <summary>
    /// When OnRelease returns false, the object is destroyed and removed from the pool, and OnDestroy is invoked.
    /// Before the fix: the return value was discarded and the object was wrongly put back into the shard queue, so the next borrow could still get the "bad object".
    /// After the fix: when OnRelease=false destroys the object and the pool is drained, auto-scaling maintains MinPoolSize,
    ///       and the next Acquire gets a freshly created object (D1 secondary-defect fix).
    /// </summary>
    [Fact]
    public void T01_OnRelease_FalseReturned_ObjectDestroyedAndOnDestroyInvoked()
    {
        var policy = new RejectOnReleasePolicy<TestObject>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)        // pre-warm creates 1 object, ensuring the first Acquire succeeds
            .WithMaxSize(4)
            .WithPolicy(policy)
            .WithEnableMetrics(true)
            .Build();

        // Act
        var obj = pool.Acquire();
        pool.Release(obj);

        // Assert: the policy's OnRelease should be called and return false;
        //       OnDestroy should be called (destroy path).
        // Note: the old assertion required PooledCount==0, but the D1 fix (auto-scaling refills the level after destroy)
        //      refills the pool, so PooledCount is no longer 0; using NotSame proves the refill took effect.
        Assert.True(policy.OnReleaseInvoked);
        Assert.True(policy.OnDestroyInvoked);

        // D1 check: after destroy the pool should auto-scale to maintain its level and successfully lend a fresh object (not the rejected one).
        var obj2 = pool.Acquire();
        Assert.NotSame(obj, obj2);
    }

    /// <summary>
    /// HayatePoolOptions adds the ScaleDownStep field with a default of 5, and IsValid still passes.
    /// </summary>
    [Fact]
    public void T02_Options_ScaleDownStep_HasDefaultOfFive()
    {
        var options = new HayatePoolOptions();
        Assert.Equal(5, options.ScaleDownStep);
        Assert.Equal(5, options.ScaleUpStep);
        Assert.True(options.IsValid(), "default ScaleDownStep=5 should make IsValid pass");
    }

    /// <summary>
    /// ThresholdScalingStrategy's scale-down branch uses ScaleDownStep instead of mistakenly using ScaleUpStep.
    /// Before the fix: scale-down = currentSize - ScaleUpStep (10), and the result was clamped to MinPoolSize.
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

        // currentSize=20, idleCount=18, usage=0.1 (below ScaleDownThreshold 0.5) -> scale down
        // expected newSize = max(20 - 3, MinPoolSize=1) = 17 (before the fix it would be 10, clamped to 1)
        var newSize = strategy.CalculateNewSize(currentSize: 20, idleCount: 18, options: options);
        Assert.Equal(17, newSize);

        // change ScaleDownStep to 7, expect 13
        options.ScaleDownStep = 7;
        newSize = strategy.CalculateNewSize(currentSize: 20, idleCount: 18, options: options);
        Assert.Equal(13, newSize);
    }

    /// <summary>
    /// When currentSize &lt;= 0 no NaN/Infinity is produced; currentSize is returned directly.
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
    /// At construction, PreWarm evenly distributes _maxSize across shardCount.
    /// The fix for UpdateShardMaxSizes's index-out-of-range aims to keep behavior consistent on the dynamic scale-up/down paths;
    /// since the construction path PreWarm and UpdateShardMaxSizes use the same formula, this asserts via reflection
    /// that _shards[i]._maxSize equals MaxPoolSize/ShardCount for all i (no overflow, no misallocation).
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

        var shardType = shards.GetType().GetElementType()!;
        var maxSizeField = shardType.GetField("_maxSize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(maxSizeField);

        for (var i = 0; i < shards.Length; i++)
        {
            var max = (int)maxSizeField.GetValue(shards.GetValue(i))!;
            Assert.Equal(25, max);
        }
    }

    /// <summary>
    /// D2: HayatePoolBuilder.WithScaleDownStep should correctly write options.ScaleDownStep,
    /// aligned with WithScaleUpStep's behavior; an invalid value (&lt; 1) throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void D2_Builder_WithScaleDownStep_WritesOption()
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithScaleDownStep(7);

        var optionsField = typeof(HayatePoolBuilder<TestObject>)
            .GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(optionsField);
        var options = (HayatePoolOptions)optionsField.GetValue(builder)!;
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
    /// Test policy: OnRelease always returns false (rejects return to pool) and exposes an OnDestroy call flag.
    /// </summary>
    private sealed class RejectOnReleasePolicy<T> : IHayateObjectPolicy<T> where T : class
    {
        public bool OnReleaseInvoked { get; private set; }
        public bool OnDestroyInvoked { get; private set; }

        public T Create() => (T)Activator.CreateInstance(typeof(T))!;

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
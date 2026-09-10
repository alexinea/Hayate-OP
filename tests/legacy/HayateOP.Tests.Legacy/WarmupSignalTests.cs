using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M17：预热就绪信号（WaitForWarmup）。
/// <para>
/// 语义：<c>true</c> 时预热转后台执行，构造立即返回；借出（同步/异步）在预热完成前阻塞，
/// 预热失败时信号同样置位（不出现永久阻塞）。默认 <c>false</c> 与 2.4 行为完全一致。
/// </para>
/// </summary>
public class WarmupSignalTests
{
    private class GateObject { }

    /// <summary>创建过程被外部 gate 阻塞的策略——用于确定性地证明「预热未完成 → 借出阻塞」。</summary>
    private sealed class GatedPolicy : IHayateObjectPolicy<GateObject>
    {
        private readonly ManualResetEventSlim _gate;
        public int CreatedCount;

        public GatedPolicy(ManualResetEventSlim gate) => _gate = gate;

        public GateObject Create()
        {
            _gate.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Increment(ref CreatedCount);
            return new GateObject();
        }

        public bool OnRelease(GateObject item) => true;
        public bool Validate(GateObject item) => true;
        public void OnAcquire(GateObject item) { }
        public void OnPassivate(GateObject item) { }
        public void OnDestroy(GateObject item) { }
    }

    /// <summary>创建必失败的策略——用于验证预热失败路径不会把借出永久挂住。</summary>
    private sealed class ThrowingPolicy : IHayateObjectPolicy<GateObject>
    {
        public GateObject Create() => throw new InvalidOperationException("create failed (M17 test)");
        public bool OnRelease(GateObject item) => true;
        public bool Validate(GateObject item) => true;
        public void OnAcquire(GateObject item) { }
        public void OnPassivate(GateObject item) { }
        public void OnDestroy(GateObject item) { }
    }

    private static HayatePoolBuilder<GateObject> BaseBuilder(string name)
        => new HayatePoolBuilder<GateObject>()
            .WithPoolName(name)
            .WithMinSize(4)
            .WithMaxSize(4)
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithCreationRetryCount(1);

    [Fact]
    public void WaitForWarmup_DefaultIsFalse()
    {
        Assert.False(new HayatePoolOptions().WaitForWarmup);
    }

    [Fact]
    public void WaitForWarmup_False_ShouldKeepSynchronousPrewarm()
    {
        // 零破坏：默认不等待 → 构造返回时预热已完成（同步预热）
        using var pool = BaseBuilder("m17-default").Build();

        Assert.Equal(4, pool.GetStats().CurrentSize);
        var item = pool.Acquire();
        pool.Release(item);
    }

    [Fact]
    public void WaitForWarmup_True_ShouldBlockAcquireUntilWarmupCompletes()
    {
        using var gate = new ManualResetEventSlim(false);
        var policy = new GatedPolicy(gate);

        using var pool = BaseBuilder("m17-gated")
            .WithPolicy(policy)
            .WithWaitForWarmup(true)
            .Build();

        // 构造已返回而预热被 gate 阻塞 → 尚无对象创建（后台预热确实与构造解耦）
        Assert.Equal(0, policy.CreatedCount);

        var acquire = Task.Run(() => pool.Acquire());

        // 预热未完成期间借出必须阻塞（就绪门生效；L5 冷池自举让位）
        Assert.False(acquire.Wait(TimeSpan.FromMilliseconds(500)), "预热未完成时 Acquire 不应返回");

        // 放行预热 → 就绪信号置位 → 借出放行
        gate.Set();
        Assert.True(acquire.Wait(TimeSpan.FromSeconds(15)), "预热完成后 Acquire 应放行");
        Assert.Equal(4, policy.CreatedCount);
    }

    [Fact]
    public async Task WaitForWarmup_True_ShouldGateAcquireAsync()
    {
        using var gate = new ManualResetEventSlim(false);
        var policy = new GatedPolicy(gate);

        using var pool = BaseBuilder("m17-gated-async")
            .WithPolicy(policy)
            .WithWaitForWarmup(true)
            .Build();

        var pending = pool.AcquireAsync();
        await Task.Delay(300);
        Assert.False(pending.IsCompleted, "预热未完成时 AcquireAsync 不应完成");

        gate.Set();
        var item = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(item);
        Assert.Equal(4, policy.CreatedCount);
    }

    [Fact]
    public void WaitForWarmup_True_ShouldReleaseGate_WhenWarmupFails()
    {
        using var pool = BaseBuilder("m17-fail-warmup")
            .WithPolicy(new ThrowingPolicy())
            .WithWaitForWarmup(true)
            .Build();

        // 预热全程创建失败 → 就绪信号仍须置位，借出不得永久挂起（按拒绝语义快速失败）
        var outcome = Task.Run(() =>
        {
            try
            {
                pool.Acquire(TimeSpan.FromMilliseconds(200));
                return "acquired";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        });

        Assert.True(outcome.Wait(TimeSpan.FromSeconds(15)), "预热失败后 Acquire 不得永久阻塞");
    }
}

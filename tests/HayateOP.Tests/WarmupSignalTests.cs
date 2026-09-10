using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Warm-up ready signal (WaitForWarmup).
/// <para>
/// Semantics: when <c>true</c>, prewarming runs in the background and construction returns immediately; borrows (sync/async) block until prewarming completes,
/// and if prewarming fails the signal is still set (no permanent block). The default <c>false</c> is fully identical to the 2.4 behavior.
/// </para>
/// </summary>
public class WarmupSignalTests
{
    private class GateObject { }

    /// <summary>A policy whose creation is blocked by an external gate -- used to deterministically prove "prewarm incomplete -> borrow blocks".</summary>
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

    /// <summary>A policy whose creation always fails -- used to verify the prewarm-failure path does not hang borrows forever.</summary>
    private sealed class ThrowingPolicy : IHayateObjectPolicy<GateObject>
    {
        public GateObject Create() => throw new InvalidOperationException("create failed (warmup test)");
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

    [Fact(Timeout = 30_000)]
    public void WaitForWarmup_DefaultIsFalse()
    {
        Assert.False(new HayatePoolOptions().WaitForWarmup);
    }

    [Fact(Timeout = 30_000)]
    public void WaitForWarmup_False_ShouldKeepSynchronousPrewarm()
    {
        // Zero regression: by default it does not wait -> prewarming is already complete when construction returns (synchronous prewarm)
        using var pool = BaseBuilder("m17-default").Build();

        Assert.Equal(4, pool.GetStats().CurrentSize);
        var item = pool.Acquire();
        pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void WaitForWarmup_True_ShouldBlockAcquireUntilWarmupCompletes()
    {
        using var gate = new ManualResetEventSlim(false);
        var policy = new GatedPolicy(gate);

        using var pool = BaseBuilder("m17-gated")
            .WithPolicy(policy)
            .WithWaitForWarmup(true)
            .Build();

        // Construction has returned but prewarming is blocked by the gate -> no object created yet (background prewarm is truly decoupled from construction)
        Assert.Equal(0, policy.CreatedCount);

        var acquire = Task.Run(() => pool.Acquire());

        // During prewarm the borrow must block (the ready gate is in effect; the cold-pool bootstrap yields)
        Assert.False(acquire.Wait(TimeSpan.FromMilliseconds(500)), "Acquire should not return before prewarm completes");

        // Release the gate -> the ready signal is set -> the borrow is released
        gate.Set();
        Assert.True(acquire.Wait(TimeSpan.FromSeconds(15)), "Acquire should be released after prewarm completes");
        Assert.Equal(4, policy.CreatedCount);
    }

    [Fact(Timeout = 30_000)]
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
        Assert.False(pending.IsCompleted, "AcquireAsync should not complete before prewarm completes");

        gate.Set();
        var item = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(item);
        Assert.Equal(4, policy.CreatedCount);
    }

    [Fact(Timeout = 30_000)]
    public void WaitForWarmup_True_ShouldReleaseGate_WhenWarmupFails()
    {
        using var pool = BaseBuilder("m17-fail-warmup")
            .WithPolicy(new ThrowingPolicy())
            .WithWaitForWarmup(true)
            .Build();

        // Prewarm fails to create throughout -> the ready signal must still be set, and borrows must not hang forever (fast-fail per the rejection semantics)
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

        Assert.True(outcome.Wait(TimeSpan.FromSeconds(15)), "Acquire must not block forever after prewarm fails");
    }
}

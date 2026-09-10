using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Warm-up ready signal (WaitForWarmup).
/// <para>
/// Semantics: when <c>true</c>, warm-up moves to the background and construction returns immediately; borrows (sync/async) block until warm-up completes,
/// and the signal is also set on warm-up failure (no permanent blocking). The default <c>false</c> matches the 2.4 behavior exactly.
/// </para>
/// </summary>
public class WarmupSignalTests
{
    private class GateObject { }

    /// <summary>Policy whose creation is blocked by an external gate -- used to deterministically prove "warm-up incomplete -> borrow blocks".</summary>
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

    /// <summary>Policy whose creation always fails -- used to verify the warm-up failure path does not hang borrows forever.</summary>
    private sealed class ThrowingPolicy : IHayateObjectPolicy<GateObject>
    {
        public GateObject Create() => throw new InvalidOperationException("create failed (warm-up failure path)");
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
        // Zero breakage: by default it does not wait -> warm-up is already complete when construction returns (synchronous warm-up)
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

        // Construction has returned but warm-up is gated -> no objects created yet (background warm-up is truly decoupled from construction)
        Assert.Equal(0, policy.CreatedCount);

        var acquire = Task.Run(() => pool.Acquire());

        // During warm-up the borrow must block (the ready gate takes effect; the cold-pool bootstrap yields)
        Assert.False(acquire.Wait(TimeSpan.FromMilliseconds(500)), "Acquire must not return before warm-up completes");

        // release the warm-up gate -> ready signal is set -> borrow is released
        gate.Set();
        Assert.True(acquire.Wait(TimeSpan.FromSeconds(15)), "Acquire should be released after warm-up completes");
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
        Assert.False(pending.IsCompleted, "AcquireAsync must not complete before warm-up completes");

        gate.Set();
#if NET48
        // net48 lacks Task<T>.WaitAsync(TimeSpan) (introduced in .NET 6); use WhenAny + Delay as an equivalent timeout wait;
        // net6.0/net7.0 and other higher versions use the native WaitAsync in the #else branch.
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(pending, completed);
        var item = await pending;
#else
        var item = await pending.WaitAsync(TimeSpan.FromSeconds(15));
#endif
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

        // Creation fails throughout warm-up -> the ready signal must still be set and borrows must not hang forever (fail fast per the reject semantics)
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

        Assert.True(outcome.Wait(TimeSpan.FromSeconds(15)), "Acquire must not block forever after warm-up fails");
    }
}

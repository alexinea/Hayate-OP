using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

public class AsyncPolicyReturnTests
{
    private sealed class TestObject
    {
    }

    private abstract class AsyncPolicyStub : IHayateAsyncObjectPolicy<TestObject>
    {
        public virtual TestObject Create() => new();
        public virtual ValueTask<TestObject> CreateAsync(CancellationToken cancellationToken = default) => new(new TestObject());
        public virtual bool OnRelease(TestObject item) => true;
        public virtual ValueTask<bool> OnReleaseAsync(TestObject item, CancellationToken cancellationToken = default) => new(true);
        public virtual bool Validate(TestObject item) => true;
        public virtual void OnAcquire(TestObject item) { }
        public virtual void OnPassivate(TestObject item) { }
        public virtual ValueTask OnPassivateAsync(TestObject item, CancellationToken cancellationToken = default) => default;
        public virtual void OnDestroy(TestObject item) { }
        public virtual ValueTask OnDestroyAsync(TestObject item, CancellationToken cancellationToken = default) => default;
    }

    private sealed class GatedReturnPolicy : AsyncPolicyStub
    {
        private readonly TaskCompletionSource<bool> _passivateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _asyncPassivateCalls;
        private int _asyncReleaseCalls;
        private int _syncPassivateCalls;
        private int _syncReleaseCalls;

        public int AsyncPassivateCalls => Volatile.Read(ref _asyncPassivateCalls);
        public int AsyncReleaseCalls => Volatile.Read(ref _asyncReleaseCalls);
        public int SyncPassivateCalls => Volatile.Read(ref _syncPassivateCalls);
        public int SyncReleaseCalls => Volatile.Read(ref _syncReleaseCalls);

        public void OpenPassivate() => _passivateGate.TrySetResult(true);
        public void OpenRelease() => _releaseGate.TrySetResult(true);

        public override void OnPassivate(TestObject item) => Interlocked.Increment(ref _syncPassivateCalls);
        public override bool OnRelease(TestObject item)
        {
            Interlocked.Increment(ref _syncReleaseCalls);
            return true;
        }

        public override async ValueTask OnPassivateAsync(TestObject item, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncPassivateCalls);
            await _passivateGate.Task.ConfigureAwait(false);
        }

        public override async ValueTask<bool> OnReleaseAsync(TestObject item, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncReleaseCalls);
            await _releaseGate.Task.ConfigureAwait(false);
            return true;
        }
    }

    private sealed class RejectingAsyncPolicy : AsyncPolicyStub
    {
        private int _asyncPassivateCalls;
        private int _asyncReleaseCalls;
        private int _syncPassivateCalls;
        private int _syncReleaseCalls;

        public int AsyncPassivateCalls => Volatile.Read(ref _asyncPassivateCalls);
        public int AsyncReleaseCalls => Volatile.Read(ref _asyncReleaseCalls);
        public int SyncPassivateCalls => Volatile.Read(ref _syncPassivateCalls);
        public int SyncReleaseCalls => Volatile.Read(ref _syncReleaseCalls);

        public override void OnPassivate(TestObject item) => Interlocked.Increment(ref _syncPassivateCalls);
        public override bool OnRelease(TestObject item)
        {
            Interlocked.Increment(ref _syncReleaseCalls);
            return true;
        }

        public override ValueTask OnPassivateAsync(TestObject item, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncPassivateCalls);
            return default;
        }

        public override ValueTask<bool> OnReleaseAsync(TestObject item, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncReleaseCalls);
            return new ValueTask<bool>(false);
        }
    }

    private sealed class SyncOnlyPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _passivateCalls;
        private int _releaseCalls;

        public int PassivateCalls => Volatile.Read(ref _passivateCalls);
        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public TestObject Create() => new();
        public bool OnRelease(TestObject item)
        {
            Interlocked.Increment(ref _releaseCalls);
            return true;
        }

        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) => Interlocked.Increment(ref _passivateCalls);
        public void OnDestroy(TestObject item) { }
    }

    private sealed class AlwaysReadyStrategy : IHayatePreparationStrategy<TestObject>
    {
        public Task<bool> IsReadyAsync(TestObject item, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PrepareAsync(TestObject item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SyncOnlyPool : IHayateObjectPool<TestObject>
    {
        private int _releaseCalls;

        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public TestObject Acquire() => new();
        public TestObject Acquire(TimeSpan timeout) => Acquire();
        public Task<TestObject> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult(Acquire());
        public Task<TestObject> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(Acquire());
        public void Release(TestObject item) => Interlocked.Increment(ref _releaseCalls);
        public HayatePoolStats GetStats() => new();
        public HayatePoolSnapshot TakeSnapshot() => new();
        public void ReloadConfig(Action<HayatePoolOptions> configure) { }
        public void Clear() { }
        public HayatePoolOptions GetOptions() => new();
        public bool CheckAvailable() => true;
        public void SetUnavailable(string? reason = null) { }
        public void SetAvailable() { }
        public int Evict(HayateEvictReason reason) => 0;
        public int PreWarm(int count) => 0;
        public void Dispose() { }
    }

    private static IHayateObjectPool<TestObject> Build(IHayateObjectPolicy<TestObject> policy, bool lean = false)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithShardCount(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithPolicy(policy);

        if (lean) builder.WithLean();
        return builder.Build();
    }

    private static async Task ReturnAfterOpeningGatesAsync(HayatePoolScope<TestObject> scope, GatedReturnPolicy policy)
    {
        var dispose = scope.DisposeAsync().AsTask();

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncPassivateCalls > 0, 5_000),
            "the return never invoked OnPassivateAsync");
        Assert.False(dispose.IsCompleted,
            "the return completed although OnPassivateAsync had not finished");

        policy.OpenPassivate();

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncReleaseCalls > 0, 5_000),
            "the return never invoked OnReleaseAsync");
        Assert.False(dispose.IsCompleted,
            "the return completed although OnReleaseAsync had not finished");

        policy.OpenRelease();
        await dispose;
    }

    private static async Task DisposeWithAwaitUsingAsync(IHayateObjectPool<TestObject> pool)
    {
        await using var scope = await pool.AcquireScopeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncPolicy_AsyncLease_AwaitsBothReturnHooks()
    {
        var policy = new GatedReturnPolicy();
        using var pool = Build(policy);
        var scope = await pool.AcquireScopeAsync();

        await ReturnAfterOpeningGatesAsync(scope, policy);

        Assert.Equal(1, policy.AsyncPassivateCalls);
        Assert.Equal(1, policy.AsyncReleaseCalls);
        Assert.Equal(0, policy.SyncPassivateCalls);
        Assert.Equal(0, policy.SyncReleaseCalls);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void AsyncPolicy_SyncRelease_WaitsOnBothAsynchronousHooks()
    {
        var policy = new GatedReturnPolicy();
        using var pool = Build(policy);
        var item = pool.Acquire();

        Exception? failure = null;
        var releaser = new Thread(() =>
        {
            try { pool.Release(item); }
            catch (Exception ex) { failure = ex; }
        });
        releaser.IsBackground = true;
        releaser.Start();

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncPassivateCalls > 0, 5_000),
            "the synchronous return never invoked OnPassivateAsync");
        Assert.False(releaser.Join(TimeSpan.FromMilliseconds(200)),
            "the synchronous return completed although OnPassivateAsync had not finished");

        policy.OpenPassivate();

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncReleaseCalls > 0, 5_000),
            "the synchronous return never invoked OnReleaseAsync");
        Assert.False(releaser.Join(TimeSpan.FromMilliseconds(200)),
            "the synchronous return completed although OnReleaseAsync had not finished");

        policy.OpenRelease();

        Assert.True(releaser.Join(TimeSpan.FromSeconds(15)),
            "the synchronous return did not finish after the asynchronous hooks completed");
        Assert.Null(failure);
        Assert.Equal(0, policy.SyncPassivateCalls);
        Assert.Equal(0, policy.SyncReleaseCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncPolicy_LeanAwaitUsing_AwaitsBothReturnHooks()
    {
        var policy = new GatedReturnPolicy();
        using var pool = Build(policy, lean: true);
        var dispose = DisposeWithAwaitUsingAsync(pool);

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncPassivateCalls > 0, 5_000),
            "the lean await-using return never invoked OnPassivateAsync");
        Assert.False(dispose.IsCompleted,
            "the lean await-using return completed although OnPassivateAsync had not finished");

        policy.OpenPassivate();

        Assert.True(SpinWait.SpinUntil(() => policy.AsyncReleaseCalls > 0, 5_000),
            "the lean await-using return never invoked OnReleaseAsync");
        Assert.False(dispose.IsCompleted,
            "the lean await-using return completed although OnReleaseAsync had not finished");

        policy.OpenRelease();
        await dispose;

        Assert.Equal(0, policy.SyncPassivateCalls);
        Assert.Equal(0, policy.SyncReleaseCalls);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task PreparationPool_AsyncLease_ForwardsTheAwaitedReturn()
    {
        var policy = new GatedReturnPolicy();
        var inner = Build(policy);
        using var pool = new HayatePreparationPool<TestObject>(inner, new AlwaysReadyStrategy());
        var scope = await pool.AcquireScopeAsync();

        await ReturnAfterOpeningGatesAsync(scope, policy);

        Assert.Equal(1, policy.AsyncPassivateCalls);
        Assert.Equal(1, policy.AsyncReleaseCalls);
        Assert.Equal(0, policy.SyncPassivateCalls);
        Assert.Equal(0, policy.SyncReleaseCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncPolicy_RejectingAsyncRelease_DestroysInsteadOfRetaining()
    {
        var policy = new RejectingAsyncPolicy();
        using var pool = Build(policy);
        var scope = await pool.AcquireScopeAsync();

        await scope.DisposeAsync();

        Assert.Equal(1, policy.AsyncPassivateCalls);
        Assert.Equal(1, policy.AsyncReleaseCalls);
        Assert.Equal(0, policy.SyncPassivateCalls);
        Assert.Equal(0, policy.SyncReleaseCalls);
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task SyncOnlyPolicy_AsyncLease_KeepsTheSynchronousReturnPath()
    {
        var policy = new SyncOnlyPolicy();
        using var pool = Build(policy);
        var scope = await pool.AcquireScopeAsync();

        await scope.DisposeAsync();

        Assert.Equal(1, policy.PassivateCalls);
        Assert.Equal(1, policy.ReleaseCalls);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncLease_ThirdPartyPool_FallsBackToSynchronousRelease()
    {
        using var pool = new SyncOnlyPool();
        var scope = await pool.AcquireScopeAsync();

        await scope.DisposeAsync();

        Assert.Equal(1, pool.ReleaseCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncLease_DisposeAsync_ReturnsTheObjectExactlyOnce()
    {
        var policy = new GatedReturnPolicy();
        using var pool = Build(policy);
        var scope = await pool.AcquireScopeAsync();

        var firstReturn = scope.DisposeAsync().AsTask();
        Assert.True(SpinWait.SpinUntil(() => policy.AsyncPassivateCalls > 0, 5_000),
            "the first return never invoked OnPassivateAsync");

        var duplicateReturn = scope.DisposeAsync();
        Assert.True(duplicateReturn.IsCompleted,
            "a duplicate asynchronous dispose should be a no-op");

        policy.OpenPassivate();
        Assert.True(SpinWait.SpinUntil(() => policy.AsyncReleaseCalls > 0, 5_000),
            "the first return never invoked OnReleaseAsync");
        policy.OpenRelease();
        await firstReturn;

        Assert.Equal(1, policy.AsyncPassivateCalls);
        Assert.Equal(1, policy.AsyncReleaseCalls);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }
}

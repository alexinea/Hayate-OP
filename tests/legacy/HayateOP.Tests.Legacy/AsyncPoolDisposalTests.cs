#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// A2 -- the asynchronous disposal contract (docs/async-policy.md §5). <c>DisposeAsync</c> drains:
    /// every object the pool owns is disposed preferring <c>IAsyncDisposable</c> over
    /// <c>IDisposable</c>, and the policy's asynchronous destroy hook is awaited when it provides one.
    /// The synchronous <c>Dispose()</c> keeps its current semantics for every pool, and a policy that
    /// does not implement the asynchronous contract keeps the exact synchronous destroy statements it
    /// always ran (rule 2).
    ///
    /// The gate is what makes "awaited" observable from the outside: the drain cannot finish until the
    /// test opens it, so a <c>DisposeAsync</c> that returned early, or that blocked a thread instead of
    /// awaiting, is caught by the incompleteness assertions below rather than passing quietly.
    ///
    /// The whole file is gated on net6.0+: on net48 the interface does not exist, and this compilation unit
    /// must still compile — the same fork the engine takes (#if NET6_0_OR_GREATER) applies to its tests.
    /// </summary>
    public class AsyncPoolDisposalTests
    {
        /// <summary>
        /// Aggregate disposal counters shared by every object a single test creates, so the assertions
        /// see one picture instead of chasing per-object state.
        /// </summary>
        private sealed class DisposalCounter
        {
            private int _syncDisposes;
            private int _asyncDisposes;

            public int SyncDisposes { get { return Volatile.Read(ref _syncDisposes); } }
            public int AsyncDisposes { get { return Volatile.Read(ref _asyncDisposes); } }

            public void OnSyncDispose() { Interlocked.Increment(ref _syncDisposes); }
            public void OnAsyncDispose() { Interlocked.Increment(ref _asyncDisposes); }
        }

        /// <summary>
        /// A pooled object that implements both disposal contracts and reports which one ran: the
        /// asynchronous drain must prefer the asynchronous one, while the unchanged synchronous drain
        /// keeps calling the synchronous one.
        /// </summary>
        private sealed class DualDisposeObject : IDisposable, IAsyncDisposable
        {
            private readonly DisposalCounter _counter;

            /// <summary>
            /// The builder's <c>new()</c> constraint needs this; the tests always pass the counting
            /// constructor instead, so the detached counter only ever sees objects nobody borrowed.
            /// </summary>
            public DualDisposeObject() : this(new DisposalCounter()) { }

            public DualDisposeObject(DisposalCounter counter) { _counter = counter; }

            public void Dispose() { _counter.OnSyncDispose(); }

            public ValueTask DisposeAsync()
            {
                _counter.OnAsyncDispose();
                return default;
            }
        }

        /// <summary>
        /// Answers every hook affirmatively and creates objects bound to the shared disposal counter;
        /// the policies below override only the destroy behaviour under test, which keeps each of them
        /// readable against the ten members the two contracts declare together.
        /// </summary>
        private abstract class AsyncPolicyStub : IHayateAsyncObjectPolicy<DualDisposeObject>
        {
            protected AsyncPolicyStub(DisposalCounter counter) { Counter = counter; }

            protected DisposalCounter Counter { get; }

            public virtual DualDisposeObject Create() { return new DualDisposeObject(Counter); }

            public virtual ValueTask<DualDisposeObject> CreateAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<DualDisposeObject>(new DualDisposeObject(Counter));
            }

            public virtual bool OnRelease(DualDisposeObject item) { return true; }
            public virtual ValueTask<bool> OnReleaseAsync(DualDisposeObject item, CancellationToken cancellationToken = default) { return new ValueTask<bool>(true); }
            public virtual bool Validate(DualDisposeObject item) { return true; }
            public virtual void OnAcquire(DualDisposeObject item) { }
            public virtual void OnPassivate(DualDisposeObject item) { }
            public virtual ValueTask OnPassivateAsync(DualDisposeObject item, CancellationToken cancellationToken = default) { return default; }
            public virtual void OnDestroy(DualDisposeObject item) { }
            public virtual ValueTask OnDestroyAsync(DualDisposeObject item, CancellationToken cancellationToken = default) { return default; }
        }

        /// <summary>
        /// Async-capable policy whose asynchronous destroy hook parks on a gate until the test opens it,
        /// and whose synchronous destroy hook counts calls so the engine's preference is observable.
        /// </summary>
        private sealed class GatedDestroyPolicy : AsyncPolicyStub
        {
            private readonly TaskCompletionSource<bool> _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _asyncDestroyCalls;
            private int _syncDestroyCalls;

            public GatedDestroyPolicy(DisposalCounter counter) : base(counter) { }

            public int AsyncDestroyCalls { get { return Volatile.Read(ref _asyncDestroyCalls); } }
            public int SyncDestroyCalls { get { return Volatile.Read(ref _syncDestroyCalls); } }

            public void Open() { _gate.TrySetResult(true); }

            public override void OnDestroy(DualDisposeObject item) { Interlocked.Increment(ref _syncDestroyCalls); }

            public override async ValueTask OnDestroyAsync(DualDisposeObject item, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _asyncDestroyCalls);
                await _gate.Task.ConfigureAwait(false);
            }
        }

        /// <summary>The pre-A1 contract: no asynchronous hook exists on this policy at all.</summary>
        private sealed class SyncOnlyPolicy : IHayateObjectPolicy<DualDisposeObject>
        {
            private readonly DisposalCounter _counter;
            private int _destroyCalls;

            public SyncOnlyPolicy(DisposalCounter counter) { _counter = counter; }

            public int DestroyCalls { get { return Volatile.Read(ref _destroyCalls); } }

            public DualDisposeObject Create() { return new DualDisposeObject(_counter); }
            public bool OnRelease(DualDisposeObject item) { return true; }
            public bool Validate(DualDisposeObject item) { return true; }
            public void OnAcquire(DualDisposeObject item) { }
            public void OnPassivate(DualDisposeObject item) { }
            public void OnDestroy(DualDisposeObject item) { Interlocked.Increment(ref _destroyCalls); }
        }

        /// <summary>A preparation strategy that hands every object out as ready, so the borrow is the inner pool's plain acquire.</summary>
        private sealed class AlwaysReadyStrategy : IHayatePreparationStrategy<DualDisposeObject>
        {
            public Task<bool> IsReadyAsync(DualDisposeObject item, CancellationToken cancellationToken = default) { return Task.FromResult(true); }
            public Task PrepareAsync(DualDisposeObject item, CancellationToken cancellationToken = default) { return Task.CompletedTask; }
        }

        /// <summary>
        /// A third-party style inner pool that implements only the synchronous contract. On net6+ every
        /// built-in pool implements <c>IHayateAsyncObjectPool&lt;T&gt;</c> whatever its policy, so this is
        /// the only inner shape that exercises the decorator's synchronous fallback.
        /// </summary>
        private sealed class SyncOnlyInnerPool : IHayateObjectPool<DualDisposeObject>
        {
            private readonly DisposalCounter _counter;
            private readonly List<DualDisposeObject> _idle = new List<DualDisposeObject>();
            private readonly object _gate = new object();

            public SyncOnlyInnerPool(DisposalCounter counter) { _counter = counter; }

            public DualDisposeObject Acquire()
            {
                lock (_gate)
                {
                    if (_idle.Count > 0)
                    {
                        var item = _idle[_idle.Count - 1];
                        _idle.RemoveAt(_idle.Count - 1);
                        return item;
                    }
                }
                return new DualDisposeObject(_counter);
            }

            public DualDisposeObject Acquire(TimeSpan timeout) { return Acquire(); }
            public Task<DualDisposeObject> AcquireAsync(CancellationToken cancellationToken = default) { return Task.FromResult(Acquire()); }
            public Task<DualDisposeObject> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { return Task.FromResult(Acquire()); }
            public void Release(DualDisposeObject item) { lock (_gate) { _idle.Add(item); } }
            public void Clear() { Dispose(); }
            public void Dispose()
            {
                lock (_gate)
                {
                    foreach (var item in _idle)
                    {
                        item.Dispose();
                    }
                    _idle.Clear();
                }
            }
            public HayatePoolStats GetStats() { return new HayatePoolStats(); }
            public HayatePoolSnapshot TakeSnapshot() { return new HayatePoolSnapshot(); }
            public void ReloadConfig(Action<HayatePoolOptions> configure) { }
            public HayatePoolOptions GetOptions() { return new HayatePoolOptions(); }
            public bool CheckAvailable() { return true; }
            public void SetUnavailable(string? reason = null) { }
            public void SetAvailable() { }
            public int Evict(HayateEvictReason reason) { return 0; }
            public int PreWarm(int count) { return 0; }
        }

        /// <summary>The unbounded pool requires a parameterless constructor and disposes nothing anyway.</summary>
        private sealed class PlainObject
        {
        }

        /// <summary>
        /// Borrows <paramref name="count"/> objects simultaneously and then returns them all, so that
        /// many distinct objects sit idle when the drain runs — a sequential acquire/release round-trip
        /// would keep handing back the same single object and park only one.
        /// </summary>
        private static void ParkIdle(IHayateObjectPool<DualDisposeObject> pool, int count)
        {
            var items = new DualDisposeObject[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = pool.Acquire();
            }
            for (var i = 0; i < count; i++)
            {
                pool.Release(items[i]);
            }
        }

        [Fact(Timeout = 30_000)]
        public async Task AsyncPolicy_DisposeAsync_AwaitsTheDestroyHookWhileItDrains()
        {
            var counter = new DisposalCounter();
            var policy = new GatedDestroyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("async-dispose-drain")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 3);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<DualDisposeObject>>(pool);
            var dispose = asyncPool.DisposeAsync();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncDestroyCalls > 0, 5_000),
                "the drain never invoked the asynchronous destroy hook");
            // Parked on the gate: the drain neither completed with half-torn-down objects nor blocked a
            // thread on the hook.
            Assert.False(dispose.IsCompleted,
                "DisposeAsync completed although the destroy hook had not finished -- the hook is not being awaited");

            policy.Open();

            await dispose;
            Assert.Equal(3, policy.AsyncDestroyCalls);
            // Rule 1: the asynchronous hook is the one the engine calls, and the synchronous one is not a
            // second divergent drain.
            Assert.Equal(0, policy.SyncDestroyCalls);
            // Each object was torn down through its asynchronous disposal, never the synchronous one.
            Assert.Equal(3, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);
        }

        [Fact(Timeout = 30_000)]
        public async Task SyncOnlyPolicy_DisposeAsync_PrefersAsyncDisposalWithoutASynchronousHookCall()
        {
            var counter = new DisposalCounter();
            var policy = new SyncOnlyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("sync-policy-async-drain")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 2);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<DualDisposeObject>>(pool);
            await asyncPool.DisposeAsync();

            // The general-mode drain runs no destroy hook — exactly like the synchronous clear it
            // mirrors — while the object disposal still prefers the asynchronous form
            // (docs/async-policy.md §5), because the caller explicitly chose the asynchronous drain.
            Assert.Equal(0, policy.DestroyCalls);
            Assert.Equal(2, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);
        }

        [Fact]
        public void SyncOnlyPolicy_Dispose_KeepsTheSynchronousDrainUnchanged()
        {
            var counter = new DisposalCounter();
            var policy = new SyncOnlyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("sync-dispose-unchanged")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 2);

            pool.Dispose();

            // Rule 2: no hook, no asynchronous disposal, the exact statements the drain has always run.
            Assert.Equal(0, policy.DestroyCalls);
            Assert.Equal(2, counter.SyncDisposes);
            Assert.Equal(0, counter.AsyncDisposes);
        }

        [Fact]
        public void AsyncPolicy_Dispose_KeepsTheBareDrainWhilePreferringAsyncDisposal()
        {
            var counter = new DisposalCounter();
            var policy = new GatedDestroyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("async-policy-sync-dispose")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 2);

            pool.Dispose();

            // The synchronous clear runs no destroy hook — for any policy, exactly as before — but a
            // pool whose policy opted into the asynchronous contract tears its objects down through
            // IAsyncDisposable even here (docs/async-policy.md §5).
            Assert.Equal(0, policy.AsyncDestroyCalls);
            Assert.Equal(0, policy.SyncDestroyCalls);
            Assert.Equal(2, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);
        }

        [Fact(Timeout = 30_000)]
        public async Task AsyncPolicy_LeanDisposeAsync_DrainsThroughTheAwaitedDestroyHook()
        {
            var counter = new DisposalCounter();
            var policy = new GatedDestroyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("lean-async-dispose")
                .WithLean()
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 2);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<DualDisposeObject>>(pool);
            var dispose = asyncPool.DisposeAsync();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncDestroyCalls > 0, 5_000),
                "the lean drain never invoked the asynchronous destroy hook");
            Assert.False(dispose.IsCompleted,
                "the lean DisposeAsync completed although the destroy hook had not finished -- the hook is not being awaited");

            policy.Open();

            await dispose;
            Assert.Equal(2, policy.AsyncDestroyCalls);
            Assert.Equal(0, policy.SyncDestroyCalls);
            Assert.Equal(2, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);
        }

        [Fact]
        public void AsyncPolicy_LeanClear_RunsTheDestroySiteThroughTheAsynchronousHook()
        {
            var counter = new DisposalCounter();
            var policy = new GatedDestroyPolicy(counter);
            var pool = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("lean-clear-rule1")
                .WithLean()
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            ParkIdle(pool, 1);

            Exception? failure = null;
            var clearer = new Thread(() =>
            {
                try { pool.Clear(); }
                catch (Exception ex) { failure = ex; }
            });
            clearer.IsBackground = true;
            clearer.Start();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncDestroyCalls > 0, 5_000),
                "the synchronous lean clear never invoked the asynchronous destroy hook");
            // Rule 1 from the synchronous entry point: the clear waits on the asynchronous hook instead
            // of calling the synchronous OnDestroy.
            Assert.False(clearer.Join(TimeSpan.FromMilliseconds(200)),
                "the synchronous clear returned although the destroy hook had not finished");

            policy.Open();

            Assert.True(clearer.Join(TimeSpan.FromSeconds(15)),
                "the synchronous clear did not finish after the destroy hook completed");
            Assert.Null(failure);
            Assert.Equal(1, policy.AsyncDestroyCalls);
            Assert.Equal(0, policy.SyncDestroyCalls);
            Assert.Equal(1, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);

            pool.Dispose();
        }

        [Fact(Timeout = 30_000)]
        public async Task PreparationPool_DisposeAsync_ForwardsToTheInnerAsynchronousDrain()
        {
            var counter = new DisposalCounter();
            var policy = new GatedDestroyPolicy(counter);
            var inner = new HayatePoolBuilder<DualDisposeObject>()
                .WithPoolName("prep-inner-async")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithShardCount(1)
                .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
                .WithPolicy(policy)
                .Build();
            var pool = new HayatePreparationPool<DualDisposeObject>(inner, new AlwaysReadyStrategy());
            ParkIdle(pool, 2);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<DualDisposeObject>>(pool);
            var dispose = asyncPool.DisposeAsync();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncDestroyCalls > 0, 5_000),
                "the forwarded drain never invoked the asynchronous destroy hook");
            Assert.False(dispose.IsCompleted,
                "the forwarded DisposeAsync completed although the destroy hook had not finished -- the decorator is not awaiting the inner drain");

            policy.Open();

            await dispose;
            Assert.Equal(2, policy.AsyncDestroyCalls);
            Assert.Equal(2, counter.AsyncDisposes);
            Assert.Equal(0, counter.SyncDisposes);
        }

        [Fact(Timeout = 30_000)]
        public async Task PreparationPool_DisposeAsync_FallsBackToTheSynchronousTeardown()
        {
            var counter = new DisposalCounter();
            var inner = new SyncOnlyInnerPool(counter);
            var pool = new HayatePreparationPool<DualDisposeObject>(inner, new AlwaysReadyStrategy());
            ParkIdle(pool, 2);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<DualDisposeObject>>(pool);
            await asyncPool.DisposeAsync();

            // An inner pool without the asynchronous contract keeps its synchronous teardown: the
            // decorator falls back to Dispose instead of inventing a drain of its own.
            Assert.Equal(2, counter.SyncDisposes);
            Assert.Equal(0, counter.AsyncDisposes);
        }

        [Fact(Timeout = 30_000)]
        public async Task UnboundedPool_DisposeAsync_ClearsTheRetainedObjects()
        {
            var pool = new HayateUnboundedPool<PlainObject>();
            Assert.Equal(4, pool.PreWarm(4));
            Assert.Equal(4, pool.PooledCount);

            var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<PlainObject>>(pool);
            var dispose = asyncPool.DisposeAsync();

            // Reference-drop model: dropping the references is the whole destroy, so the task is already
            // complete and the objects themselves are never disposed.
            Assert.True(dispose.IsCompleted);
            await dispose;
            Assert.Equal(0, pool.PooledCount);
        }
    }
}
#endif

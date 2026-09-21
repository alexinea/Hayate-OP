#if NET6_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// A1 -- the asynchronous creation contract. A policy that implements
    /// <see cref="IHayateAsyncObjectPolicy{T}"/> is driven through <c>CreateAsync</c> from every creation
    /// path: the asynchronous borrow genuinely awaits the hook, and a synchronous borrow waits on that same
    /// hook instead of calling the synchronous <c>Create</c>. A policy that does not implement the interface
    /// keeps the synchronous creation path untouched.
    ///
    /// The gate is what makes "awaited" observable from the outside: creation cannot finish until the test
    /// opens it, so a borrow that returned early, or that blocked a thread instead of awaiting, is caught by
    /// the incompleteness assertions below rather than passing quietly.
    ///
    /// The whole file is gated on net6.0+: on net48 the interface does not exist, and this compilation unit
    /// must still compile — the same fork the engine takes (#if NET6_0_OR_GREATER) applies to its tests.
    /// </summary>
    public class AsyncPolicyCreationTests
    {
        private class TestObject
        {
            public int Data { get; set; }
        }

        /// <summary>
        /// Answers every hook affirmatively. The policies below override only the creation behaviour under
        /// test, which keeps each of them readable against the ten members the two contracts declare together.
        /// </summary>
        private abstract class AsyncPolicyStub : IHayateAsyncObjectPolicy<TestObject>
        {
            public virtual TestObject Create() => new TestObject();

            public virtual ValueTask<TestObject> CreateAsync(CancellationToken cancellationToken = default)
                => new ValueTask<TestObject>(new TestObject());

            public virtual bool OnRelease(TestObject item) => true;
            public virtual ValueTask<bool> OnReleaseAsync(TestObject item, CancellationToken cancellationToken = default) => new ValueTask<bool>(true);
            public virtual bool Validate(TestObject item) => true;
            public virtual void OnAcquire(TestObject item) { }
            public virtual void OnPassivate(TestObject item) { }
            public virtual ValueTask OnPassivateAsync(TestObject item, CancellationToken cancellationToken = default) => default;
            public virtual void OnDestroy(TestObject item) { }
            public virtual ValueTask OnDestroyAsync(TestObject item, CancellationToken cancellationToken = default) => default;
        }

        /// <summary>Async-only policy whose creation parks on a gate until the test opens it.</summary>
        private sealed class GatedAsyncPolicy : AsyncPolicyStub
        {
            private readonly TaskCompletionSource<bool> _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _asyncCreateCalls;
            private int _syncCreateCalls;
            private int _acquireCalls;

            public int AsyncCreateCalls => Volatile.Read(ref _asyncCreateCalls);
            public int SyncCreateCalls => Volatile.Read(ref _syncCreateCalls);
            public int AcquireCalls => Volatile.Read(ref _acquireCalls);

            public void Open() => _gate.TrySetResult(true);

            public override TestObject Create()
            {
                Interlocked.Increment(ref _syncCreateCalls);
                return new TestObject();
            }

            public override async ValueTask<TestObject> CreateAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _asyncCreateCalls);
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new TestObject();
            }

            public override void OnAcquire(TestObject item) => Interlocked.Increment(ref _acquireCalls);
        }

        /// <summary>Async-only policy whose creation never finishes on its own and honours the caller's token.</summary>
        private sealed class HangingAsyncPolicy : AsyncPolicyStub
        {
            private int _asyncCreateCalls;

            public int AsyncCreateCalls => Volatile.Read(ref _asyncCreateCalls);

            public override async ValueTask<TestObject> CreateAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _asyncCreateCalls);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new TestObject();
            }
        }

        /// <summary>Policy that implements both contracts; the counters say which hook the engine preferred.</summary>
        private sealed class DualContractPolicy : AsyncPolicyStub
        {
            private int _asyncCreateCalls;
            private int _syncCreateCalls;

            public int AsyncCreateCalls => Volatile.Read(ref _asyncCreateCalls);
            public int SyncCreateCalls => Volatile.Read(ref _syncCreateCalls);

            public override TestObject Create()
            {
                Interlocked.Increment(ref _syncCreateCalls);
                return new TestObject();
            }

            public override ValueTask<TestObject> CreateAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _asyncCreateCalls);
                return new ValueTask<TestObject>(new TestObject());
            }
        }

        /// <summary>The pre-A1 contract: no asynchronous hook exists on this policy at all.</summary>
        private sealed class SyncOnlyPolicy : IHayateObjectPolicy<TestObject>
        {
            private int _createCalls;

            public int CreateCalls => Volatile.Read(ref _createCalls);

            public TestObject Create()
            {
                Interlocked.Increment(ref _createCalls);
                return new TestObject();
            }

            public bool OnRelease(TestObject item) => true;
            public bool Validate(TestObject item) => true;
            public void OnAcquire(TestObject item) { }
            public void OnPassivate(TestObject item) { }
            public void OnDestroy(TestObject item) { }
        }

        [Fact(Timeout = 30_000)]
        public async Task AsyncPolicy_AsyncBorrow_AwaitsCreateAsync()
        {
            var policy = new GatedAsyncPolicy();
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("async-creation-borrow")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(policy)
                .Build();

            var acquire = pool.AcquireAsync();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncCreateCalls > 0, 5_000),
                "the asynchronous borrow never invoked CreateAsync");
            // Parked on the gate: the borrow neither completed with a half-created object nor blocked a
            // thread on the creation.
            Assert.False(acquire.IsCompleted,
                "the borrow completed although creation had not finished -- creation is not being awaited");

            policy.Open();

            var item = await acquire;
            Assert.NotNull(item);
            Assert.Equal(1, policy.AsyncCreateCalls);
            Assert.Equal(0, policy.SyncCreateCalls);
            Assert.Equal(1, policy.AcquireCalls);
        }

        // No Timeout here: xunit v2 rejects it on a non-async test. Every wait inside is bounded instead
        // (SpintUntil 5s, Join 15s, and the in-thread acquire timeout).
        [Fact]
        public void AsyncPolicy_SyncBorrow_WaitsOnTheSameAsynchronousHook()
        {
            var policy = new GatedAsyncPolicy();
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("async-creation-sync-borrow")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(policy)
                .Build();

            TestObject? borrowed = null;
            Exception? failure = null;
            var borrower = new Thread(() =>
            {
                try { borrowed = pool.Acquire(TimeSpan.FromSeconds(20)); }
                catch (Exception ex) { failure = ex; }
            });
            borrower.IsBackground = true;
            borrower.Start();

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncCreateCalls > 0, 5_000),
                "the synchronous borrow never invoked CreateAsync");
            // Rule 1: the synchronous entry point waits on the asynchronous hook rather than calling Create.
            Assert.False(borrower.Join(TimeSpan.FromMilliseconds(200)),
                "the synchronous borrow returned although creation had not finished");

            policy.Open();

            Assert.True(borrower.Join(TimeSpan.FromSeconds(15)),
                "the synchronous borrow did not finish after creation completed");
            Assert.Null(failure);
            Assert.NotNull(borrowed);
            Assert.Equal(1, policy.AsyncCreateCalls);
            Assert.Equal(0, policy.SyncCreateCalls);
        }

        [Fact(Timeout = 30_000)]
        public async Task DualContractPolicy_AsyncHookWinsWhereTheyOverlap()
        {
            var policy = new DualContractPolicy();
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("async-creation-dual")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(policy)
                .Build();

            var item = await pool.AcquireAsync();

            Assert.NotNull(item);
            Assert.Equal(1, policy.AsyncCreateCalls);
            Assert.Equal(0, policy.SyncCreateCalls);
        }

        [Fact(Timeout = 30_000)]
        public async Task AsyncPolicy_LeanMode_DrivesBothBorrowStylesThroughCreateAsync()
        {
            var asyncPolicy = new GatedAsyncPolicy();
            using (var leanAsync = new HayatePoolBuilder<TestObject>()
                .WithLean()
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(asyncPolicy)
                .Build())
            {
                var acquire = leanAsync.AcquireAsync();

                Assert.True(SpinWait.SpinUntil(() => asyncPolicy.AsyncCreateCalls > 0, 5_000),
                    "the lean asynchronous borrow never invoked CreateAsync");
                Assert.False(acquire.IsCompleted,
                    "the lean asynchronous borrow completed although creation had not finished");

                asyncPolicy.Open();

                Assert.NotNull(await acquire);
                Assert.Equal(0, asyncPolicy.SyncCreateCalls);
            }

            var syncPolicy = new GatedAsyncPolicy();
            using var leanSync = new HayatePoolBuilder<TestObject>()
                .WithLean()
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(syncPolicy)
                .Build();

            TestObject? borrowed = null;
            Exception? failure = null;
            var borrower = new Thread(() =>
            {
                try { borrowed = leanSync.Acquire(TimeSpan.FromSeconds(20)); }
                catch (Exception ex) { failure = ex; }
            });
            borrower.IsBackground = true;
            borrower.Start();

            Assert.True(SpinWait.SpinUntil(() => syncPolicy.AsyncCreateCalls > 0, 5_000),
                "the lean synchronous borrow never invoked CreateAsync");
            Assert.False(borrower.Join(TimeSpan.FromMilliseconds(200)),
                "the lean synchronous borrow returned although creation had not finished");

            syncPolicy.Open();

            Assert.True(borrower.Join(TimeSpan.FromSeconds(15)),
                "the lean synchronous borrow did not finish after creation completed");
            Assert.Null(failure);
            Assert.NotNull(borrowed);
            Assert.Equal(0, syncPolicy.SyncCreateCalls);
        }

        [Fact(Timeout = 30_000)]
        public async Task AsyncPolicy_CancellationDuringCreation_PropagatesWithoutConsumingRetries()
        {
            var policy = new HangingAsyncPolicy();
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("async-creation-cancel")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(policy)
                .Build();

            using var cts = new CancellationTokenSource();
            var acquire = pool.AcquireAsync(cts.Token);

            Assert.True(SpinWait.SpinUntil(() => policy.AsyncCreateCalls > 0, 5_000),
                "the asynchronous borrow never invoked CreateAsync");

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
            // A cancellation is not a creation failure: it must not be retried (or logged and wrapped).
            Assert.Equal(1, policy.AsyncCreateCalls);
        }

        [Fact(Timeout = 30_000)]
        public async Task SyncOnlyPolicy_KeepsTheSynchronousCreationPath()
        {
            var policy = new SyncOnlyPolicy();
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("sync-policy-unchanged")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithPolicy(policy)
                .Build();

            var item = await pool.AcquireAsync();

            Assert.NotNull(item);
            Assert.Equal(1, policy.CreateCalls);

            // And the object round-trips through the pool as before: reused, not re-created.
            pool.Release(item);
            Assert.Same(item, pool.Acquire());
            Assert.Equal(1, policy.CreateCalls);
        }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the optional lifecycle-observer contract (G-22, research item 33-R-21). A policy that
/// also implements <see cref="IHayateObjectLifecycleObserver{T}"/> is told <i>why</i> each pooled object
/// was destroyed; a policy that does not implement it compiles and behaves exactly as it did before.
/// </summary>
/// <remarks>
/// <para>
/// The two hooks partition <see cref="HayateDestroyReason"/>: a validation failure arrives through
/// <c>OnValidationFailed</c> and never through <c>OnEvict</c>, every other destruction arrives through
/// <c>OnEvict</c>. Each test therefore asserts the reason it expects <b>and</b> that the other hook
/// stayed silent, which turns the partition into a checked property instead of a convention.
/// </para>
/// <para>
/// Two sites in the engine are deliberately not driven here because they are unreachable from the public
/// surface by construction: the park that loses to a concurrent clear (a window of a few instructions
/// between two reads of the front-tier generation) and the pre-warm overflow that the surrounding clamp
/// makes impossible. Both are noted in the delivery record rather than asserted.
/// </para>
/// </remarks>
public class ObjectLifecycleObserverTests
{
    /// <summary>A pooled object that records its own disposal, so a test can prove the observer ran while
    /// the value was still alive.</summary>
    private sealed class TestObject : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The policy under test: a full <see cref="IHayateObjectPolicy{T}"/> that also implements the
    /// observer contract, recording every notification so a test can assert on the reason.</summary>
    private sealed class ObservingPolicy : IHayateObjectPolicy<TestObject>, IHayateObjectLifecycleObserver<TestObject>
    {
        private readonly List<(TestObject Item, HayateDestroyReason Reason)> _evicted = new();
        private readonly List<TestObject> _validationFailures = new();
        private readonly List<string> _order = new();

        private int _validateAttempts;
        private int _destroyed;

        /// <summary>How many leading <c>Validate</c> calls answer <c>false</c>; 0 (the default) means
        /// every object is valid.</summary>
        public int FailFirstValidations { get; set; }

        /// <summary>What <c>OnRelease</c> answers; <c>false</c> makes the pool destroy every return.</summary>
        public bool AcceptReturn { get; set; } = true;

        /// <summary>When set, both observer hooks throw, so the robustness of the destroy path can be
        /// asserted.</summary>
        public bool ThrowFromObserver { get; set; }

        /// <summary>Set when a hook was handed an object that had already been disposed.</summary>
        public bool SawADisposedValue { get; private set; }

        public int Destroyed => Volatile.Read(ref _destroyed);

        public IReadOnlyList<(TestObject Item, HayateDestroyReason Reason)> Evicted
        {
            get { lock (_evicted) return _evicted.ToArray(); }
        }

        public IReadOnlyList<TestObject> ValidationFailures
        {
            get { lock (_validationFailures) return _validationFailures.ToArray(); }
        }

        /// <summary>The hook names in the order they were called, so ordering can be asserted.</summary>
        public IReadOnlyList<string> Order
        {
            get { lock (_order) return _order.ToArray(); }
        }

        public TestObject Create() => new TestObject();

        public bool OnRelease(TestObject item) => AcceptReturn;

        public bool Validate(TestObject item) => Interlocked.Increment(ref _validateAttempts) > FailFirstValidations;

        public void OnAcquire(TestObject item) { }

        public void OnPassivate(TestObject item) { }

        public void OnDestroy(TestObject item)
        {
            Interlocked.Increment(ref _destroyed);
            lock (_order) _order.Add("destroy");
        }

        public void OnEvict(TestObject item, HayateDestroyReason reason)
        {
            if (item.Disposed) SawADisposedValue = true;
            lock (_order) _order.Add("evict");
            lock (_evicted) _evicted.Add((item, reason));
            if (ThrowFromObserver) throw new InvalidOperationException("the observer threw");
        }

        public void OnValidationFailed(TestObject item)
        {
            if (item.Disposed) SawADisposedValue = true;
            lock (_order) _order.Add("validation-failed");
            lock (_validationFailures) _validationFailures.Add(item);
            if (ThrowFromObserver) throw new InvalidOperationException("the observer threw");
        }
    }

    /// <summary>The counter-example: the same policy without the observer contract. Everything about it
    /// must stay exactly as it was before the contract existed.</summary>
    private sealed class PlainPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _destroyed;

        public int Destroyed => Volatile.Read(ref _destroyed);

        public TestObject Create() => new TestObject();

        public bool OnRelease(TestObject item) => true;

        public bool Validate(TestObject item) => true;

        public void OnAcquire(TestObject item) { }

        public void OnPassivate(TestObject item) { }

        public void OnDestroy(TestObject item) => Interlocked.Increment(ref _destroyed);
    }

    /// <summary>Bounded poll: waits for a background concern to produce a notification, without pinning a
    /// fixed sleep to a timer period.</summary>
    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    // ── Evicted ─────────────────────────────────────────────────────────────────────────────────────

    [Theory(Timeout = 30_000)]
    [InlineData(HayateEvictReason.Touched)]
    [InlineData(HayateEvictReason.Idle)]
    [InlineData(HayateEvictReason.Expired)]
    public void OnEvict_ShouldReportEvicted_WhenThePoolEvictsByCategory(HayateEvictReason reason)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-evict-" + reason)
            .WithPolicy(policy)
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false);

        if (reason == HayateEvictReason.Idle) builder = builder.WithMaxIdleTime(TimeSpan.FromMilliseconds(150));
        if (reason == HayateEvictReason.Expired) builder = builder.WithMaxLifeTime(TimeSpan.FromMilliseconds(150));

        using var pool = builder.Build();

        var borrowed = pool.Acquire();
        pool.Release(borrowed);
        if (reason != HayateEvictReason.Touched) Thread.Sleep(300);

        var evicted = pool.Evict(reason);

        Assert.True(evicted > 0, $"Evict({reason}) should have removed at least one idle object");
        var events = policy.Evicted;
        Assert.Equal(evicted, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.Evicted, e.Reason));
        Assert.Contains(events, e => ReferenceEquals(e.Item, borrowed));
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportEvicted_WhenTheFrontTierSweepRemovesTheObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-evict-front-tier")
            .WithPolicy(policy)
            .WithThreadLocalFrontSlot()
            .WithMinSize(0)
            .WithMaxSize(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var parked = pool.Acquire();
        pool.Release(parked);       // parked in this thread's front slot, out of every shard's sight

        var evicted = pool.Evict(HayateEvictReason.Touched);

        Assert.Equal(1, evicted);
        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(parked, events[0].Item);
        Assert.Equal(HayateDestroyReason.Evicted, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 60_000)]
    public void OnEvict_ShouldReportEvicted_WhenTheBackgroundRunRetiresIdleObjects()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-background-evict")
            .WithPolicy(policy)
            .WithMinSize(4)
            .WithMaxSize(8)
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
            .WithEnableAutoScaling(false)
            .Build();

        Assert.True(SpinUntil(() => policy.Evicted.Count > 0),
            "the background eviction run never notified the observer");

        var events = policy.Evicted;
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.Evicted, e.Reason));
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 60_000)]
    public void OnEvict_ShouldReportEvicted_WhenTheBackgroundRunSweepsTheFrontTier()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-background-tier")
            .WithPolicy(policy)
            .WithThreadLocalFrontSlot()
            .WithMinSize(0)
            .WithMaxSize(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
            .WithEnableAutoScaling(false)
            .Build();

        var parked = pool.Acquire();
        pool.Release(parked);       // parked in this thread's front slot

        Assert.True(SpinUntil(() => policy.Evicted.Count > 0),
            "the background run never swept the front tier");

        var events = policy.Evicted;
        Assert.Contains(events, e => ReferenceEquals(e.Item, parked));
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.Evicted, e.Reason));
    }

    // ── ValidationFailed (its own hook, never OnEvict) ──────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void OnValidationFailed_ShouldFire_WhenTheBorrowPathRejectsAnObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { FailFirstValidations = 1 };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-validate-borrow")
            .WithPolicy(policy)
            .WithMinSize(1)         // the borrow has to take an idle object: the create-on-miss path does not validate
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableValidation()
            .WithValidateOnBorrow()
            .WithEnableGenerationOptimization(false)    // a generation-1 object skips validation otherwise
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();

        Assert.NotNull(obj);
        Assert.Single(policy.ValidationFailures);
        Assert.Empty(policy.Evicted);
        Assert.Equal(1, policy.Destroyed);
    }

#if NET6_0_OR_GREATER
    [Fact(Timeout = 30_000)]
    public async Task OnValidationFailed_ShouldFire_WhenTheAsynchronousBorrowPathRejectsAnObject()
    {
        var policy = new ObservingPolicy { FailFirstValidations = 1 };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-validate-borrow-async")
            .WithPolicy(policy)
            .WithMinSize(1)         // the borrow has to take an idle object: the create-on-miss path does not validate
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableValidation()
            .WithValidateOnBorrow()
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = await pool.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Single(policy.ValidationFailures);
        Assert.Empty(policy.Evicted);
    }
#endif

    [Fact(Timeout = 30_000)]
    public void OnValidationFailed_ShouldFire_WhenTheReturnPathRejectsAnObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-validate-return")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableValidation()
            .WithValidateOnReturn()
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        policy.FailFirstValidations = 1;        // the next Validate call answers false

        pool.Release(obj);

        Assert.Single(policy.ValidationFailures);
        Assert.Same(obj, policy.ValidationFailures[0]);
        Assert.Empty(policy.Evicted);
        Assert.Equal(1, policy.Destroyed);
    }

    [Fact(Timeout = 60_000)]
    public void OnValidationFailed_ShouldFire_WhenTheIdleSweepFindsAnInvalidObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-validate-idle")
            .WithPolicy(policy)
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableValidation()
            .WithValidateWhileIdle()
            .WithValidateInterval(1000)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        policy.FailFirstValidations = 2;        // both idle objects fail the sweep

        Assert.True(SpinUntil(() => policy.ValidationFailures.Count >= 2),
            "the idle validation sweep never notified the observer");

        Assert.Equal(2, policy.ValidationFailures.Count);
        Assert.Empty(policy.Evicted);
    }

    // ── ReturnRejected / CapacityOverflow / Rotated / Abandoned / ScaledDown / Misuse ───────────────

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportReturnRejected_WhenThePolicyRefusesTheReturn()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { AcceptReturn = false };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-return-rejected")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(obj, events[0].Item);
        Assert.Equal(HayateDestroyReason.ReturnRejected, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportCapacityOverflow_WhenTheSoftCeilingIsReached()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-soft-ceiling")
            .WithPolicy(policy)
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithSoftCapacity(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var first = pool.Acquire();
        var second = pool.Acquire();

        pool.Release(first);        // the ceiling still has room
        pool.Release(second);       // the pool already holds SoftCapacity idle objects

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(second, events[0].Item);
        Assert.Equal(HayateDestroyReason.CapacityOverflow, events[0].Reason);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportCapacityOverflow_WhenTheShardHasNoRoom()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-shard-full")
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var first = pool.Acquire();
        var second = pool.Acquire();

        pool.Release(first);        // the single shard takes it
        pool.Release(second);       // the single shard is at its idle capacity

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(second, events[0].Item);
        Assert.Equal(HayateDestroyReason.CapacityOverflow, events[0].Reason);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportRotated_WhenAnExpiredObjectIsReplacedOnBorrow()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-rotated")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(50))
            .WithEnableLifetimeRotationOnBorrow()
            .Build();

        var first = pool.Acquire();
        Thread.Sleep(150);          // held well past MaxLifeTime
        pool.Release(first);

        var second = pool.Acquire();

        Assert.NotSame(first, second);
        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(first, events[0].Item);
        Assert.Equal(HayateDestroyReason.Rotated, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportAbandoned_WhenRecoveryReclaimsALeakedObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-abandoned")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnMaintenance()
            .WithRemoveAbandonedInterval(50)
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();

        Assert.True(SpinUntil(() => policy.Evicted.Count > 0),
            "abandoned recovery never notified the observer");

        var events = policy.Evicted;
        Assert.Contains(events, e => ReferenceEquals(e.Item, abandoned));
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.Abandoned, e.Reason));
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 60_000)]
    public void OnEvict_ShouldReportScaledDown_WhenThePoolShrinks()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-scaled-down")
            .WithPolicy(policy)
            .WithEnableAutoScaling(true)
            .WithMinSize(2)
            .WithMaxSize(30)
            .WithScalingInterval(200)
            .WithScaleDownThreshold(0.5)
            .WithScaleDownStep(5)
            .WithScaleDownCooldownSeconds(0)
            .WithScaleUpCooldownSeconds(0)
            .WithEnableEviction(false)
            .Build();

        var leases = new List<TestObject>();
        for (var i = 0; i < 10; i++) leases.Add(pool.Acquire());
        foreach (var o in leases) pool.Release(o);

        Assert.True(SpinUntil(() => policy.Evicted.Count > 0, 20_000),
            "auto-scaling never notified the observer");

        var events = policy.Evicted;
        Assert.Contains(events, e => e.Reason == HayateDestroyReason.ScaledDown);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportMisuse_WhenAForeignObjectIsReturned()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-misuse")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var foreign = new TestObject();
        pool.Release(foreign);

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(foreign, events[0].Item);
        Assert.Equal(HayateDestroyReason.Misuse, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportMisuse_WhenAnObjectIsReturnedTwice()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-double-return")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        Assert.Equal(1, pool.GetStats().PooledCount);

        // The shard's append guard refuses the second return -- the object is already idle in the
        // list -- and the destroy that follows is a caller mistake, not capacity pressure, so the
        // reason is Misuse rather than CapacityOverflow.
        pool.Release(obj);

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(obj, events[0].Item);
        Assert.Equal(HayateDestroyReason.Misuse, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
        Assert.True(obj.Disposed);
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    // ── PoolCleared ─────────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportPoolCleared_WhenThePoolIsCleared()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-cleared")
            .WithPolicy(policy)
            .WithMinSize(3)
            .WithMaxSize(6)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        Assert.Empty(policy.Evicted);

        pool.Clear();

        var events = policy.Evicted;
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.PoolCleared, e.Reason));
        Assert.Contains(events, e => ReferenceEquals(e.Item, obj));
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportPoolCleared_WhenThePoolIsDisposed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-disposed")
            .WithPolicy(policy)
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        pool.Dispose();

        var events = policy.Evicted;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.PoolCleared, e.Reason));
    }

    /// <summary>
    /// The drain hands the objects parked in the front tiers back to their shards first. When a shard has
    /// no room for one — the slot store outlived a shrink — the object is destroyed outright, and that is
    /// a distinct destroy site from the shard's own clear.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportPoolCleared_WhenTheFrontTierDrainFindsNoRoom()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-tier-drain-overflow")
            .WithPolicy(policy)
            .WithThreadLocalFrontSlot()
            .WithShardCount(1)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        // A thread that borrows once parks its returns in its own front slot from then on. The slot is
        // invisible to every other thread, which is what leaves the shard empty for the next step.
        TestObject inTheSlot = null!;
        var parker = new Thread(() =>
        {
            var borrowed = pool.Acquire();
            pool.Release(borrowed);
            inTheSlot = borrowed;
        });
        parker.Start();
        parker.Join();

        // A second thread borrows one and hands it to a third that has never borrowed: the Rents rule
        // sends that return to the shard rather than to a slot, so the shard now holds one object.
        TestObject inTheShard = null!;
        var borrower = new Thread(() => inTheShard = pool.Acquire());
        borrower.Start();
        borrower.Join();

        var handback = new Thread(() => pool.Release(inTheShard));
        handback.Start();
        handback.Join();

        Assert.NotSame(inTheSlot, inTheShard);
        Assert.Empty(policy.Evicted);

        // The shard is at capacity, so the parked object cannot go back into it and the drain destroys it.
        pool.Clear();

        var events = policy.Evicted;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.PoolCleared, e.Reason));
        Assert.Contains(events, e => ReferenceEquals(e.Item, inTheSlot));
        Assert.Contains(events, e => ReferenceEquals(e.Item, inTheShard));
    }

#if NET6_0_OR_GREATER
    [Fact(Timeout = 30_000)]
    public async Task OnEvict_ShouldReportPoolCleared_WhenThePoolIsDisposedAsynchronously()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-disposed-async")
            .WithPolicy(policy)
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var asyncPool = Assert.IsAssignableFrom<IHayateAsyncObjectPool<TestObject>>(pool);
        await asyncPool.DisposeAsync();

        var events = policy.Evicted;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.PoolCleared, e.Reason));
    }
#endif

    // ── Contract properties: ordering, robustness, opt-in ───────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void Observer_ShouldBeCalledBeforeTheObjectIsTornDown()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { AcceptReturn = false };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-ordering")
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        // The notification precedes both the policy's own destroy hook and the object's disposal.
        Assert.Equal(new[] { "evict", "destroy" }, policy.Order);
        Assert.False(policy.SawADisposedValue, "the observer was handed an object that had already been disposed");
    }

    [Fact(Timeout = 30_000)]
    public void ThrowingObserver_ShouldNotBreakTheDestruction()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { AcceptReturn = false, ThrowFromObserver = true };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-throwing-observer")
            .WithPolicy(policy)
            .WithMinSize(0)
            .WithMaxSize(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);                  // the observer throws; the release must not

        Assert.Equal(1, policy.Destroyed);  // the teardown still ran to completion

        var again = pool.Acquire();
        Assert.NotNull(again);
        pool.Release(again);
    }

    [Fact(Timeout = 30_000)]
    public void APolicyWithoutTheObserverContract_ShouldBeUnaffected()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new PlainPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-plain-policy")
            .WithPolicy(policy)
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        Assert.Equal(0, policy.Destroyed);

        pool.Clear();

        // docs/disposal.md §3: a drain disposes the values but deliberately does not run OnDestroy. The
        // observer contract is additive and left that contract exactly where it was.
        Assert.Equal(0, policy.Destroyed);
        Assert.True(obj.Disposed, "the drain should still have disposed the value itself");
    }

    // ── The lean fast path ──────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportReturnRejected_OnTheLeanPath()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { AcceptReturn = false };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-lean-rejected")
            .WithPolicy(policy)
            .WithLean()
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        var events = policy.Evicted;
        Assert.Single(events);
        Assert.Same(obj, events[0].Item);
        Assert.Equal(HayateDestroyReason.ReturnRejected, events[0].Reason);
        Assert.Empty(policy.ValidationFailures);
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportCapacityOverflow_OnTheLeanPath()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-lean-overflow")
            .WithPolicy(policy)
            .WithLean()
            .WithMinSize(0)
            .WithMaxSize(3)
            .WithSoftCapacity(1)        // the lean buffer's soft ceiling is what makes a return overflow
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        var first = pool.Acquire();
        var second = pool.Acquire();
        var third = pool.Acquire();

        pool.Release(first);        // the buffer retains it
        pool.Release(second);       // the soft ceiling is already reached
        pool.Release(third);        // and stays reached

        var events = policy.Evicted;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.CapacityOverflow, e.Reason));
        Assert.Contains(events, e => ReferenceEquals(e.Item, second));
        Assert.Contains(events, e => ReferenceEquals(e.Item, third));
    }

    [Fact(Timeout = 30_000)]
    public void OnEvict_ShouldReportPoolCleared_OnTheLeanPath()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-lean-cleared")
            .WithPolicy(policy)
            .WithLean()
            .WithMinSize(3)
            .WithMaxSize(6)
            .Build();

        pool.Clear();

        var events = policy.Evicted;
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(HayateDestroyReason.PoolCleared, e.Reason));
    }

    /// <summary>
    /// The lean fast path keeps no per-object state and never calls <c>Validate</c>, so the dedicated
    /// validation hook is unreachable there. The switch is set anyway: the assertion is that it changes
    /// nothing, rather than that the configuration is refused.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void OnValidationFailed_ShouldNeverFire_OnTheLeanPath()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new ObservingPolicy { FailFirstValidations = 1 };
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g22-lean-validation")
            .WithPolicy(policy)
            .WithLean()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableValidation()
            .WithValidateOnBorrow()
            .WithValidateOnReturn()
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        Assert.Empty(policy.ValidationFailures);
        Assert.Empty(policy.Evicted);
    }
}

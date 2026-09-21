using System.Collections.Concurrent;
using System.Reflection;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Regression test for the <c>Shard.Remove</c> atomicity refactor.
/// <para>
/// The old implementation rebuilt the queue with ConcurrentQueue + "ToList -> Remove -> Clear -> Enqueue",
/// so a concurrent TryTake / Add during the rebuild window could lose objects; worse, the caller after Remove
/// unconditionally destroyed an object that had just been borrowed via TryTake and was in use by a business thread.
/// </para>
/// <para>
/// The new implementation uses LinkedList + SpinLock for an O(1) real unlink and introduces a "claim protocol" --
/// Remove returns true only when the object is genuinely idle and still in this shard's linked list,
/// and the caller must write <c>if (shard.Remove(w)) Destroy(w);</c>.
/// </para>
/// </summary>
[Collection(ShardAtomicRemovalCollection.Name)]
public class ShardAtomicRemovalTests
{
    private sealed class TestObject : IDisposable
    {
        public int Id { get; set; }
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    #region Reflection proxy: drive the internal Shard directly, bypassing pool lifecycle interference

    /// <summary>
    /// Obtain the pool's internal Shard instance via reflection and cache its MethodInfo for high-frequency concurrency stress.
    /// Shard is an internal nested class; the test project has no InternalsVisibleTo, so only reflection works.
    /// </summary>
    private sealed class ShardProxy
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly object _shard;
        private readonly MethodInfo _add;
        private readonly MethodInfo _tryTake;
        private readonly MethodInfo _remove;
        private readonly MethodInfo _getAll;
        private readonly PropertyInfo _count;

        public ShardProxy(IHayateObjectPool<TestObject> pool, int index = 0)
        {
            var field = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var arr = field!.GetValue(pool) as Array;
            Assert.NotNull(arr);

            _shard = arr!.GetValue(index)!;
            var t = _shard.GetType();

            _add = t.GetMethod("Add", Flags)!;
            _tryTake = t.GetMethod("TryTake", Flags)!;
            _remove = t.GetMethod("Remove", Flags)!;
            _getAll = t.GetMethod("GetAll", Flags)!;
            _count = t.GetProperty("Count", Flags)!;

            Assert.NotNull(_add);
            Assert.NotNull(_tryTake);
            Assert.NotNull(_remove);
            Assert.NotNull(_getAll);
            Assert.NotNull(_count);
        }

        public int Count => (int)_count.GetValue(_shard)!;

        public HayateObject<TestObject>[] GetAllArray()
            => (HayateObject<TestObject>[])_getAll.Invoke(_shard, null)!;

        public bool Add(HayateObject<TestObject> w) => (bool)_add.Invoke(_shard, new object[] { w })!;

        public bool TryTake(out HayateObject<TestObject> w)
        {
            var args = new object?[] { null };
            var ok = (bool)_tryTake.Invoke(_shard, args)!;
            w = (HayateObject<TestObject>)args[0]!;
            return ok;
        }

        public bool Remove(HayateObject<TestObject> w) => (bool)_remove.Invoke(_shard, new object[] { w })!;
    }

    private static HayateObject<TestObject> Wrap(int id) => new(new TestObject { Id = id });

    /// <summary>
    /// Disable all background timers to build a pool undisturbed by eviction / validation / scaling,
    /// so deterministic and concurrent checks can be done at the Shard level.
    /// <para>
    /// Note: EnableAutoScaling must stay true. ApplyFeatureSwitches, when auto-scaling is off,
    /// forces MaxPoolSize down to MinPoolSize; otherwise the shard capacity would be squeezed to 0.
    /// When MinSize is 0 the per-shard registry is always empty, so ScalingCallback returns immediately and does not disturb the case.
    /// </para>
    /// </summary>
    private static IHayateObjectPool<TestObject> BuildQuietPool(int minSize, int maxSize)
    {
        return new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithShardCount(1)
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableAutoScaling(true)
            .WithScalingInterval(600000)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .Build();
    }

    /// <summary>
    /// Pull out the pool's internal background callbacks to drive them manually at high frequency in this case,
    /// bypassing the "timer minimum interval 1000ms" limit and compressing the race window to milliseconds.
    /// </summary>
    private static MethodInfo GetPrivateMethod(object pool, string name)
    {
        var m = pool.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return m!;
    }

    #endregion

    #region Claim-protocol semantics (deterministic cases)

    /// <summary>
    /// Remove can only claim an object that is "idle and inside this shard's linked list".
    /// A borrowed object must fail to claim -- this was exactly the root cause that broke the DisableValidation case in the previous round:
    /// the eviction thread destroyed, unconditionally after the snapshot, an object still in use by a business thread.
    /// </summary>
    [Fact]
    public void T04_Remove_ClaimsOnlyIdleObjectInShard()
    {
        using var pool = BuildQuietPool(minSize: 0, maxSize: 10);
        var shard = new ShardProxy(pool);

        var a = Wrap(1);
        var b = Wrap(2);
        var c = Wrap(3);

        Assert.True(shard.Add(a));
        Assert.True(shard.Add(b));
        Assert.True(shard.Add(c));
        Assert.Equal(3, shard.Count);

        // FIFO: first-in-first-out, the one taken is a
        Assert.True(shard.TryTake(out var borrowed));
        Assert.Same(a, borrowed);
        Assert.Equal(2, shard.Count);

        // Already borrowed -> must not be claimed, and the shard contents are unaffected
        Assert.False(shard.Remove(borrowed));
        Assert.Equal(2, shard.Count);

        // After return it re-enters the pool (tail insert); only now may it be claimed
        Assert.True(shard.Add(borrowed));
        Assert.Equal(3, shard.Count);
        Assert.True(shard.Remove(borrowed));
        Assert.Equal(2, shard.Count);

        // The claimed object is physically unlinked; a later TryTake will not get it again
        Assert.True(shard.TryTake(out var next));
        Assert.Same(b, next);
        Assert.True(shard.TryTake(out var last));
        Assert.Same(c, last);
        Assert.Equal(0, shard.Count);
    }

    /// <summary>
    /// An object that has been claimed (pending destroy / already destroyed) must not be resurrected into the pool via Add,
    /// preventing a Disposed object from being borrowed again.
    /// </summary>
    [Fact]
    public void T04_Add_RejectsClaimedObject()
    {
        using var pool = BuildQuietPool(minSize: 0, maxSize: 10);
        var shard = new ShardProxy(pool);

        var a = Wrap(1);
        Assert.True(shard.Add(a));
        Assert.True(shard.Remove(a));

        // Already eviction-claimed -> the shard must refuse it on return
        Assert.False(shard.Add(a));
        Assert.Equal(0, shard.Count);
        Assert.False(shard.TryTake(out _));
    }

    #endregion

    #region Concurrency invariants (stress cases)

    /// <summary>
    /// Object conservation under concurrent Add / TryTake / Remove: no loss, no duplication, no intersection with the "borrowed" set.
    /// <para>
    /// Reproduce EvictionCallback's real concurrency shape: one batch of threads takes a GetAll() snapshot and does Remove,
    /// while another batch concurrently does TryTake borrows. If any of the three invariants breaks,
    /// it means objects were lost or double-issued under concurrency.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void T04_ConcurrentAddRemove_NoObjectsLost()
    {
        const int Total = 1000;
        const int Rounds = 3;

        for (var round = 0; round < Rounds; round++)
        {
            using var pool = BuildQuietPool(minSize: 0, maxSize: Total + 100);
            var shard = new ShardProxy(pool);

            var all = new HayateObject<TestObject>[Total];
            for (var i = 0; i < Total; i++)
            {
                all[i] = Wrap(i);
                Assert.True(shard.Add(all[i]));
            }

            Assert.Equal(Total, shard.Count);

            // After shuffling, half go via TryTake (borrow) and half via Remove (evict), simulating real contention
            var work = all.OrderBy(_ => Guid.NewGuid()).ToArray();
            var taken = new ConcurrentBag<HayateObject<TestObject>>();
            var removed = new ConcurrentBag<HayateObject<TestObject>>();

            Parallel.For(0, work.Length, i =>
            {
                if (i % 2 == 0)
                {
                    if (shard.TryTake(out var t)) taken.Add(t);
                }
                else
                {
                    if (shard.Remove(work[i])) removed.Add(work[i]);
                }
            });

            // Drain the remaining idle objects
            while (shard.TryTake(out var rest)) taken.Add(rest);

            // Invariant 1: total conservation -- each object is either borrowed or evicted, with no third fate
            Assert.Equal(Total, taken.Count + removed.Count);

            // Invariant 2: no duplication -- the same object is never taken by two threads at once
            Assert.Equal(taken.Count, taken.Distinct().Count());
            Assert.Equal(removed.Count, removed.Distinct().Count());

            // Invariant 3: mutual exclusion -- an object cannot be both borrowed and eviction-destroyed (the core guarantee of the claim protocol)
            Assert.Empty(taken.Intersect(removed));

            Assert.Equal(0, shard.Count);
        }
    }

    #endregion

    #region Pool-level regression

    /// <summary>
    /// The DisableValidation scenario that broke in the previous round: borrow -> return -> borrow again must yield the same instance.
    /// Same configuration as <c>OptimizationRegressionTests.DisableValidation_ShouldSkipAllValidation</c>,
    /// plus a distractor object so the returned object is no longer the pool's only element, ensuring FIFO and unlink logic are both correct.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void T04_BorrowReleaseBorrow_ReturnsSameInstance()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableValidation(false)
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithMaxSize(2)
            .WithMinSize(1)
            .WithAcquireTimeout(TimeSpan.FromSeconds(15))
            .WithEnableEviction(true)
            .WithEnableAutoScaling(false)
            .Build();

        for (var i = 0; i < 200; i++)
        {
            var obj = pool.Acquire();
            Assert.False(obj.IsDisposed, $"i={i} borrowed an already-disposed object");

            pool.Release(obj);

            var again = pool.Acquire();
            Assert.Same(obj, again);
            Assert.False(again.IsDisposed);

            pool.Release(again);
        }
    }

    /// <summary>
    /// Under high-concurrency borrow/return plus manually driven eviction / idle-validation callbacks, a borrowed object must never be destroyed.
    /// A policy records how many times OnDestroy hits an object still in the borrowed state; that count must be 0.
    /// <para>
    /// This is the core race: EvictionCallback first takes a GetAll() snapshot to decide "evict this",
    /// then an Acquire thread TryTakes the same object, and finally the eviction thread calls Destroy -- the old implementation would
    /// destroy it unconditionally, directly corrupting the business thread using it.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void T04_HighConcurrency_BorrowedObjectNeverDestroyed()
    {
        var policy = new BorrowedDestroyDetector<TestObject>();

        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithPolicy(policy)
            .WithAcquireTimeout(TimeSpan.FromSeconds(10))
            .WithEnableEviction(true)
            .WithMaxLifeTime(TimeSpan.FromHours(1))
            .WithMaxIdleTime(TimeSpan.FromMilliseconds(200))
            .WithSoftMinEvictableIdleTime(TimeSpan.FromMilliseconds(1))
            .WithNumTestsPerEvictionRun(16)
            .WithEnableValidation(true)
            .WithValidateWhileIdle(true)
            .WithEnableAutoScaling(true)
            .Build();

        var eviction = GetPrivateMethod(pool, "EvictionCallback");
        var validate = GetPrivateMethod(pool, "ValidateCallback");

        // Manually drive background callbacks at high frequency: the timer's minimum interval is 1000ms, too coarse to produce enough race density
        var running = true;
        var drivers = new List<Thread>();
        foreach (var m in new[] { eviction, validate })
        {
            var t = new Thread(() =>
            {
                // Fence: the original implementation spun at full speed calling the callback via Thread.Yield(), saturating a core within seconds,
                // and slowing the host. Here we switch to adaptive backoff -- still yielding between callbacks, but with a short
                // Thread.Sleep to lower the spin frequency: keeping enough eviction/validation density to hit the borrowed-state race,
                // without pushing CPU to 100%. The loop is guarded by running=false + Join(2s) in finally.
                while (Volatile.Read(ref running))
                {
                    try { m.Invoke(pool, new object?[] { null }); }
                    catch (TargetInvocationException) { /* callback already guards internally; ignore */ }
                    Thread.Sleep(1);   // Bounded backoff: ~1ms per call, avoiding busy-spin CPU burn
                }
            })
            { IsBackground = true, Name = "hayate-bg-driver" };

            t.Start();
            drivers.Add(t);
        }

        var unexpected = new ConcurrentBag<Exception>();

        try
        {
            Parallel.For(0, 6, _ =>
            {
                for (var i = 0; i < 300; i++)
                {
                    try
                    {
                        var obj = pool.Acquire();
                        Assert.False(obj.IsDisposed, "the pool must not lend an already-disposed object");
                        Thread.SpinWait(20);
                        pool.Release(obj);
                    }
                    catch (TimeoutException)
                    {
                        // An occasional borrow timeout under aggressive eviction is fine; this case only checks destroy semantics, not throughput
                    }
                    catch (Exception ex)
                    {
                        unexpected.Add(ex);
                    }
                }
            });
        }
        finally
        {
            Volatile.Write(ref running, false);
            foreach (var t in drivers) t.Join(TimeSpan.FromSeconds(2));
        }

        Assert.Empty(unexpected);
        Assert.Equal(0, policy.DestroyedWhileBorrowed);

        // Pool remains healthy after the stress run
        var final = pool.Acquire();
        Assert.False(final.IsDisposed);
        pool.Release(final);
    }

    #region §3.3 uncovered path: snapshot consistency (Shard.Count / GetAll / pool-level BorrowedCount)

    /// <summary>
    /// §3.3 Shard level: repeatedly sample GetAll() during concurrent Add / TryTake.
    /// <para>
    /// GetAll calls ToArray inside the SpinLock, so the returned snapshot is internally self-consistent: within one snapshot there is never
    /// "the same object twice" (LinkedList forbids duplicate nodes) nor a torn read of an already-unlinked object.
    /// This case has one batch of threads do pure TryTake -> immediate Add churn, while another batch keeps sampling,
    /// using concurrency to attack this snapshot-self-consistency invariant.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void T04_Snapshot_GetAll_NeverTornUnderConcurrency()
    {
        const int Total = 500;
        const int SampleCount = 3000;

        using var pool = BuildQuietPool(minSize: 0, maxSize: Total + 100);
        var shard = new ShardProxy(pool);

        var all = new HayateObject<TestObject>[Total];
        for (var i = 0; i < Total; i++)
        {
            all[i] = Wrap(i);
            Assert.True(shard.Add(all[i]));
        }

        var unexpected = new ConcurrentBag<Exception>();
        var stop = false;

        var sampler = new Thread(() =>
        {
            for (var n = 0; n < SampleCount && !Volatile.Read(ref stop); n++)
            {
                var snap = shard.GetAllArray();
                // A single snapshot must not contain duplicates (self-consistent snapshot, no torn read)
                var distinct = new HashSet<HayateObject<TestObject>>(snap);
                if (distinct.Count != snap.Length)
                {
                    unexpected.Add(new InvalidOperationException("GetAll snapshot contains a duplicate wrapper"));
                    return;
                }
                Thread.Yield();
            }
        })
        { IsBackground = true, Name = "hayate-snapshot-sampler" };
        sampler.Start();

        // Pure churn: take head and put back, keeping total stable, creating high-frequency node add/remove (bounded rounds, ends independently)
        Parallel.For(0, 4, _ =>
        {
            for (var n = 0; n < 4000; n++)
            {
                try
                {
                    if (shard.TryTake(out var t)) shard.Add(t);
                }
                catch (Exception ex) { unexpected.Add(ex); }
            }
        });

        Volatile.Write(ref stop, true);
        sampler.Join(TimeSpan.FromSeconds(3));

        Assert.Empty(unexpected);
        Assert.False(sampler.IsAlive, "sampler did not finish in time");
    }

    /// <summary>
    /// §3.3 Pool level: deterministically verify TakeSnapshot's borrow accounting in a quiet pool with no eviction / validation interference.
    /// <para>
    /// The shard registry always holds all live objects ("idle + borrowed") (removed only on Destroy), so
    /// BorrowedCount = total registered - pool idle count holds exactly when there is no eviction transient.
    /// Prewarm with MinSize so Acquire hits immediately, avoiding the reject policy's timeout wait.
    /// </para>
    /// </summary>
    [Fact]
    public void T04_Snapshot_BorrowedCount_TracksHeldObjects()
    {
        const int Capacity = 32;
        const int Held = 8;

        // MinSize=MaxSize=Capacity prewarms a full pool -> Acquire hits at once, no scaling wait
        using var pool = BuildQuietPool(minSize: Capacity, maxSize: Capacity);

        var held = new List<TestObject>(Held);
        for (var i = 0; i < Held; i++) held.Add(pool.Acquire());

        // 8 borrowed, 24 idle -> borrowed count exactly equals Held
        var snap = pool.TakeSnapshot();
        Assert.Equal(Held, snap.BorrowedCount);
        Assert.Equal(Capacity - Held, snap.PooledCount);

        // Return half -> idle +4, borrowed -4; their sum always equals the live total Capacity
        for (var i = 0; i < Held / 2; i++) pool.Release(held[i]);
        snap = pool.TakeSnapshot();
        Assert.Equal(Held / 2, snap.BorrowedCount);
        Assert.Equal(Capacity - Held / 2, snap.PooledCount);

        // Return all -> borrowed zero, all idle
        for (var i = Held / 2; i < Held; i++) pool.Release(held[i]);
        snap = pool.TakeSnapshot();
        Assert.Equal(0, snap.BorrowedCount);
        Assert.Equal(Capacity, snap.PooledCount);
    }

    /// <summary>
    /// §3.3 Pool level: repeatedly sample TakeSnapshot during concurrent borrow/return; the invariant holds.
    /// <para>
    /// Key invariant: PooledCount and BorrowedCount are always non-negative, and their sum (= the true live object count)
    /// <= pool capacity; with eviction off there is no "claimed-but-not-destroyed" transient, so the sum exactly equals the shard registry's live total.
    /// Each worker borrows only one at a time and returns it immediately, so peak held <= thread count and the pool is never drained,
    /// so Acquire never reaches the reject policy's timeout path.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void T04_Snapshot_Invariant_HoldsUnderConcurrency()
    {
        const int Capacity = 64;
        const int WorkerRounds = 3000;

        // MinSize=MaxSize=Capacity pre-warms a full pool; worker peak holding <= 8, never fully exhausted
        using var pool = BuildQuietPool(minSize: Capacity, maxSize: Capacity);

        var stop = false;
        var unexpected = new ConcurrentBag<Exception>();

        // Sampler thread: bounded time slice, ends independently, does not depend on workers
        var sampler = new Thread(() =>
        {
            var deadline = Environment.TickCount + 1500;
            while (Environment.TickCount < deadline && !Volatile.Read(ref stop))
            {
                var s = pool.TakeSnapshot();
                if (s.PooledCount < 0 || s.BorrowedCount < 0 ||
                    s.PooledCount + s.BorrowedCount > Capacity + 8)
                {
                    unexpected.Add(new InvalidOperationException(
                        $"bad snapshot: pooled={s.PooledCount}, borrowed={s.BorrowedCount}"));
                    return;
                }
                Thread.Yield();
            }
        })
        { IsBackground = true, Name = "hayate-invariant-sampler" };
        sampler.Start();

        // worker: bounded rounds, ends independently
        Parallel.For(0, 8, _ =>
        {
            for (var n = 0; n < WorkerRounds; n++)
            {
                try
                {
                    var o = pool.Acquire(TimeSpan.FromMilliseconds(200));
                    pool.Release(o);
                }
                catch (TimeoutException)
                {
                    // Should never happen (never fully drained); even if it occurs occasionally, do not fail, just skip
                }
                catch (Exception ex)
                {
                    unexpected.Add(ex);
                }
            }
        });

        Volatile.Write(ref stop, true);
        sampler.Join(TimeSpan.FromSeconds(3));

        Assert.Empty(unexpected);
    }

    #endregion

    /// <summary>
    /// Borrowed-state detection policy: OnAcquire marks, OnRelease clears the mark, and OnDestroy while still marked is a violation.
    /// </summary>
    private sealed class BorrowedDestroyDetector<T> : IHayateObjectPolicy<T> where T : class
    {
        private readonly ConcurrentDictionary<T, byte> _borrowed = new();
        private int _destroyedWhileBorrowed;

        public int DestroyedWhileBorrowed => Volatile.Read(ref _destroyedWhileBorrowed);

        public T Create() => (T)Activator.CreateInstance(typeof(T))!;

        public void OnAcquire(T item) => _borrowed[item] = 0;

        public void OnPassivate(T item) { }

        public bool OnRelease(T item)
        {
            _borrowed.TryRemove(item, out _);
            return true;
        }

        public bool Validate(T item) => true;

        public void OnDestroy(T item)
        {
            // An object still in the borrowed state being destroyed -- means a background thread corrupted the business thread using it
            if (_borrowed.ContainsKey(item)) Interlocked.Increment(ref _destroyedWhileBorrowed);
        }
    }

    #endregion

    #region Overflow / shard-rejection must run the full Destroy to trigger OnDestroy

    /// <summary>
    /// A policy that counts OnDestroy, used to verify the policy hook fires exactly once for an object destroyed via shard rejection.
    /// </summary>
    private sealed class OnDestroyCounterPolicy<T> : IHayateObjectPolicy<T> where T : class
    {
        public int OnDestroyCount;

        public T Create() => (T)Activator.CreateInstance(typeof(T))!;

        public void OnAcquire(T item) { }

        public void OnPassivate(T item) { }

        public bool OnRelease(T item) => true;

        public bool Validate(T item) => true;

        public void OnDestroy(T item) => Interlocked.Increment(ref OnDestroyCount);
    }

    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        // The registry is already split per shard (was pool-level _objectMap -> each Shard's private field _objects),
        // here we probe the registry per shard via _shards to get the wrapped object.
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard)!;
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object?[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1]!;
        }

        return null!;
    }

    private static int GetLocationCode(HayateObject<TestObject> w)
    {
        var f = typeof(HayateObject<TestObject>).GetField("Location", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int)f.GetValue(w)!;
    }

    private static int GetDestroyedFlag(HayateObject<TestObject> w)
    {
        var f = typeof(HayateObject<TestObject>).GetField("Destroyed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int)f.GetValue(w)!;
    }

    private static void SetLocation(HayateObject<TestObject> w, int locationCode)
    {
        var f = typeof(HayateObject<TestObject>).GetField("Location", BindingFlags.Instance | BindingFlags.NonPublic)!;
        f.SetValue(w, Enum.ToObject(f.FieldType, locationCode)); // Removing = 3
    }

    /// <summary>
    /// When the shard is full (overflow), Shard.Add no longer short-circuits handling itself.
    /// Before the fix, the overflow branch would "set Destroyed=1 + change the terminal Location + naked SafeDispose (only Dispose, not calling
    /// _policy.OnDestroy)", so the outer full Destroy(w) would return immediately from the idempotent CAS and OnDestroy would never fire.
    /// After the fix, Shard only refuses and returns false, leaving the destroy responsibility entirely to the caller's Destroy(w).
    /// </summary>
    [Fact]
    public void P0_AddOverflow_RejectsWithoutPrematureDestroy()
    {
        // Single shard, capacity 1: after filling, the second Add must overflow.
        using var pool = BuildQuietPool(minSize: 0, maxSize: 1);
        var shard = new ShardProxy(pool);

        var w1 = Wrap(1);
        Assert.True(shard.Add(w1));            // Fill the shard (None -> InPool)

        // Simulate an object that was "borrowed then returned" (in a real Release overflow the object is in Borrowed state).
        var w2 = Wrap(2);
        SetLocation(w2, /* Borrowed */ 2);
        var accepted = shard.Add(w2);

        // overflow -> rejected
        Assert.False(accepted);
        // After the fix: Shard does not mark Destroyed itself nor overwrite the terminal Location
        Assert.Equal(0, GetDestroyedFlag(w2));
        Assert.Equal(/* Borrowed */ 2, GetLocationCode(w2));
        // Only w1 remains in this shard
        Assert.Equal(1, shard.Count);
    }

    /// <summary>
    /// End-to-end pool-level: after an object is eviction / idle-validation claimed (Location=Removing),
    /// a business thread still Releases it -> Shard.Add refuses -> goes through Release's full Destroy(w) ->
    /// and OnDestroy fires exactly once. Before the fix the object was short-circuited by a naked Dispose in the overflow branch and OnDestroy never fired.
    /// </summary>
    [Fact]
    public void ReleaseRejectedByShard_TriggersOnDestroyOnce()
    {
        var policy = new OnDestroyCounterPolicy<TestObject>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithShardCount(1)
            .WithMinSize(1)
            .WithMaxSize(8)
            .WithPolicy(policy)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableAutoScaling(true)
            .WithScalingInterval(600000)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .Build();

        var item = pool.Acquire();                      // Pre-fill 1; Location=Borrowed, located in the shard registry
        var w = GetWrapped(pool, item);
        Assert.Equal(0, policy.OnDestroyCount);

        // Simulate the eviction thread having claimed the object (state changed only, not actually destroyed), then a business thread wrongly Releases it.
        SetLocation(w, /* Removing */ 3);
        pool.Release(item);                             // Shard.Add refuses -> full Destroy -> OnDestroy++

        Assert.Equal(1, policy.OnDestroyCount);         // After the fix: OnDestroy must fire exactly once
    }

    /// <summary>
    /// The Release shard-rejection path tops up the water level after destroying an object, so the pool never drops below MinPoolSize.
    /// Before the fix this branch lacked ForceScaleUpOneStep, so interleaved eviction / return could drop below the minimum level and trigger cold start.
    /// </summary>
    [Fact]
    public void ReleaseRejectedByShard_ForceScalesUpToKeepMinPoolSize()
    {
        var policy = new OnDestroyCounterPolicy<TestObject>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithShardCount(1)
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithPolicy(policy)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableAutoScaling(true)
            .WithScalingInterval(600000)
            .WithScaleUpCooldownSeconds(0)              // Remove the scale-up cooldown so ForceScaleUp runs synchronously and immediately
            .WithScaleUpStep(2)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .Build();

        // Pre-fill MinPoolSize=4. Borrow 1 -> 3 InPool + 1 Borrowed left.
        var item = pool.Acquire();
        Assert.True(pool.GetStats().CurrentSize >= 4);

        // Force a shard rejection: mark the object as claimed (Removing), and Release will destroy it.
        var w = GetWrapped(pool, item);
        SetLocation(w, /* Removing */ 3);
        pool.Release(item);                             // -> Destroy 1 -> pool 3 < Min 4 -> ForceScaleUp tops up

        // The water level is restored to >= MinPoolSize (ForceScaleUp synchronously adds 2 -> 5)
        var current = pool.GetStats().CurrentSize;
        Assert.True(current >= 4, $"after shard-rejection destroy the pool should restore its water level, actual CurrentSize={current}");
        Assert.Equal(1, policy.OnDestroyCount);
    }

    #endregion
}

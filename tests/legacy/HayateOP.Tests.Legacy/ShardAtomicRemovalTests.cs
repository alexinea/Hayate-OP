using System.Collections.Concurrent;
using System.Reflection;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// T04 / P0-3 回归测试：<c>Shard.Remove</c> 原子化重构。
/// <para>
/// 旧实现是 ConcurrentQueue +「ToList → Remove → Clear → Enqueue」重建队列，
/// 重建窗口内并发的 TryTake / Add 会丢对象；更严重的是调用方在 Remove 之后
/// 无条件 Destroy，会把一个刚被 TryTake 借出、正被业务线程使用的对象销毁掉。
/// </para>
/// <para>
/// 新实现：LinkedList + SpinLock 做 O(1) 真实摘除，并引入「认领协议」——
/// Remove 只有在对对象确实空闲且位于本分片链表内时才返回 true，
/// 调用方必须写成 <c>if (shard.Remove(w)) Destroy(w);</c>。
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

    #region 反射代理：直接驱动内部 Shard，绕开池的生命周期干扰

    /// <summary>
    /// 通过反射获取池内部的 Shard 实例并缓存 MethodInfo，用于高频并发压测。
    /// Shard 是 internal 嵌套类，测试项目未配置 InternalsVisibleTo，只能反射调用。
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
    /// 关闭所有后台定时器，构造一个不会被驱逐 / 校验 / 扩缩容打扰的池，
    /// 便于在 Shard 层面做确定性与并发验证。
    /// <para>
    /// 注意：必须保持 EnableAutoScaling = true。ApplyFeatureSwitches 在关闭自动扩缩容时
    /// 会强制把 MaxPoolSize 钳到 MinPoolSize，否则分片容量会被压成 0。
    /// MinSize 设为 0 时各分片登记表恒为空，ScalingCallback 会立即返回，不会干扰用例。
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
    /// 取出池内部的后台回调，用于在本用例中手动高频驱动，
    /// 绕开「定时器最小间隔 1000ms」的限制，把竞态窗口压缩到毫秒级。
    /// </summary>
    private static MethodInfo GetPrivateMethod(object pool, string name)
    {
        var m = pool.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return m!;
    }

    #endregion

    #region 认领协议语义（确定性用例）

    /// <summary>
    /// Remove 只能认领「空闲且位于本分片链表内」的对象。
    /// 已借出的对象必须认领失败 —— 这正是上一轮 T04 把 DisableValidation 用例打挂的根因：
    /// 驱逐线程在快照之后无条件 Destroy，销毁了正在被业务线程使用的对象。
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

        // FIFO：先入先出，取到的是 a
        Assert.True(shard.TryTake(out var borrowed));
        Assert.Same(a, borrowed);
        Assert.Equal(2, shard.Count);

        // 已借出 → 不得认领，且分片内容不受影响
        Assert.False(shard.Remove(borrowed));
        Assert.Equal(2, shard.Count);

        // 归还后重新入池（尾插），此时才允许被认领
        Assert.True(shard.Add(borrowed));
        Assert.Equal(3, shard.Count);
        Assert.True(shard.Remove(borrowed));
        Assert.Equal(2, shard.Count);

        // 被认领的对象已物理摘除，后续 TryTake 不会再拿到它
        Assert.True(shard.TryTake(out var next));
        Assert.Same(b, next);
        Assert.True(shard.TryTake(out var last));
        Assert.Same(c, last);
        Assert.Equal(0, shard.Count);
    }

    /// <summary>
    /// 被认领（待销毁 / 已销毁）的对象不得通过 Add 复活回池，
    /// 防止 Dispose 过的对象被再次借出。
    /// </summary>
    [Fact]
    public void T04_Add_RejectsClaimedObject()
    {
        using var pool = BuildQuietPool(minSize: 0, maxSize: 10);
        var shard = new ShardProxy(pool);

        var a = Wrap(1);
        Assert.True(shard.Add(a));
        Assert.True(shard.Remove(a));

        // 已被驱逐认领 → 归还时分片必须拒绝接收
        Assert.False(shard.Add(a));
        Assert.Equal(0, shard.Count);
        Assert.False(shard.TryTake(out _));
    }

    #endregion

    #region 并发不变量（压力用例）

    /// <summary>
    /// 并发 Add / TryTake / Remove 下对象守恒：不丢、不重、不与「已借出」集合相交。
    /// <para>
    /// 复刻 EvictionCallback 的真实并发形态：一批线程拿着 GetAll() 的快照去做 Remove，
    /// 另一批线程同时 TryTake 借出。三条不变量中任意一条被打破，
    /// 都意味着对象在并发下丢失或被重复发放。
    /// </para>
    /// </summary>
    [Fact]
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

            // 打乱后一半走 TryTake（借出）、一半走 Remove（驱逐），模拟真实争抢
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

            // 排空剩余空闲对象
            while (shard.TryTake(out var rest)) taken.Add(rest);

            // 不变量 1：总数守恒 —— 每个对象要么被借出，要么被驱逐，没有第三种归宿
            Assert.Equal(Total, taken.Count + removed.Count);

            // 不变量 2：无重复 —— 同一个对象不会被两个线程同时拿到
            Assert.Equal(taken.Count, taken.Distinct().Count());
            Assert.Equal(removed.Count, removed.Distinct().Count());

            // 不变量 3：互斥 —— 不可能既被借出又被驱逐销毁（认领协议的核心保证）
            Assert.Empty(taken.Intersect(removed));

            Assert.Equal(0, shard.Count);
        }
    }

    #endregion

    #region 池级别回归

    /// <summary>
    /// 上一轮 T04 打挂的 DisableValidation 场景：借出 → 归还 → 再借出，必须拿到同一个实例。
    /// 配置与 <c>OptimizationRegressionTests.DisableValidation_ShouldSkipAllValidation</c> 一致，
    /// 再叠加一个「归还后对象不再是池中唯一元素」的干扰对象，确保 FIFO 与摘除逻辑都正确。
    /// </summary>
    [Fact]
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
            Assert.False(obj.IsDisposed, $"i={i} 借出了已释放对象");

            pool.Release(obj);

            var again = pool.Acquire();
            Assert.Same(obj, again);
            Assert.False(again.IsDisposed);

            pool.Release(again);
        }
    }

    /// <summary>
    /// 高并发借还 + 手动高频驱动驱逐 / 空闲校验回调，正被借出的对象绝不能被销毁。
    /// 用策略层记录「OnDestroy 命中仍处借出态的对象」次数，该计数必须为 0。
    /// <para>
    /// 这是 P0-3 的核心竞态：EvictionCallback 先取 GetAll() 快照判断「该驱逐」，
    /// 随后 Acquire 线程把同一对象 TryTake 借出，最后驱逐线程调用 Destroy —— 旧实现会
    /// 无条件销毁，直接破坏正在使用它的业务线程。
    /// </para>
    /// </summary>
    [Fact]
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

        // 手动高频驱动后台回调：定时器最小间隔是 1000ms，压不出足够的竞态密度
        var running = true;
        var drivers = new List<Thread>();
        foreach (var m in new[] { eviction, validate })
        {
            var t = new Thread(() =>
            {
                // P1/R1 围栏：原实现是 Thread.Yield() 满速空转调用回调，数秒内吃满一个核，
                // 拖累宿主。这里改为自适应退避——仍在每次回调间让出时间片，但引入短暂
                // Thread.Sleep 降低空转频率：既保留足够驱逐/校验密度去冲击借出态竞态，
                // 又不至于把 CPU 打到 100%。循环由 finally 中的 running=false + Join(2s) 兜底。
                while (Volatile.Read(ref running))
                {
                    try { m.Invoke(pool, new object?[] { null }); }
                    catch (TargetInvocationException) { /* 回调内部已兜底，忽略 */ }
                    Thread.Sleep(1);   // 有界退避：~1ms/次，避免忙自旋烧 CPU
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
                        Assert.False(obj.IsDisposed, "池不应借出已释放的对象");
                        Thread.SpinWait(20);
                        pool.Release(obj);
                    }
                    catch (TimeoutException)
                    {
                        // 驱逐较为激进时偶发借出超时，本用例只校验销毁语义，不校验吞吐
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

        // 池在压测后仍然健康
        var final = pool.Acquire();
        Assert.False(final.IsDisposed);
        pool.Release(final);
    }

    #region §3.3 未覆盖路径：快照一致性（Shard.Count / GetAll / 池级 BorrowedCount）

    /// <summary>
    /// §3.3 Shard 级：并发 Add / TryTake 期间反复采样 GetAll()。
    /// <para>
    /// GetAll 在 SpinLock 内 ToArray，返回的快照必然内部自洽：同一快照内不会出现
    /// 「同一对象两次」（LinkedList 不允许重复节点）也不会读到已物理摘除的撕裂态。
    /// 本用例让一批线程做 TryTake→立即 Add 归还的纯 churn，另一批线程持续抽样，
    /// 用并发破坏去冲击这条快照自洽不变量。
    /// </para>
    /// </summary>
    [Fact]
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
                // 同一次快照内不得出现重复对象（快照自洽，无撕裂读）
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

        // 纯 churn：取头再放回，保持总量稳定，制造高频节点增删（有界轮次，独立结束）
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
    /// §3.3 池级：无驱逐 / 校验干扰的安静池里，确定性校验 TakeSnapshot 的借出口径。
    /// <para>
    /// 分片登记表恒持有「空闲 + 借出」的全部存活对象（Destroy 才移除），因此
    /// BorrowedCount = 登记总数 - 池内空闲数 在无驱逐瞬态下应精确成立。
    /// 用 MinSize 预热让 Acquire 立即命中，避免触发拒绝策略的超时等待。
    /// </para>
    /// </summary>
    [Fact]
    public void T04_Snapshot_BorrowedCount_TracksHeldObjects()
    {
        const int Capacity = 32;
        const int Held = 8;

        // MinSize=MaxSize=Capacity 预热满池 → Acquire 即时命中、无需扩容等待
        using var pool = BuildQuietPool(minSize: Capacity, maxSize: Capacity);

        var held = new List<TestObject>(Held);
        for (var i = 0; i < Held; i++) held.Add(pool.Acquire());

        // 已借出 8、仍空闲 24 → 借出数精确等于 Held
        var snap = pool.TakeSnapshot();
        Assert.Equal(Held, snap.BorrowedCount);
        Assert.Equal(Capacity - Held, snap.PooledCount);

        // 归还一半 → 空闲 +4、借出 -4，二者之和恒等于存活总数 Capacity
        for (var i = 0; i < Held / 2; i++) pool.Release(held[i]);
        snap = pool.TakeSnapshot();
        Assert.Equal(Held / 2, snap.BorrowedCount);
        Assert.Equal(Capacity - Held / 2, snap.PooledCount);

        // 全部归还 → 借出归零、全部空闲
        for (var i = Held / 2; i < Held; i++) pool.Release(held[i]);
        snap = pool.TakeSnapshot();
        Assert.Equal(0, snap.BorrowedCount);
        Assert.Equal(Capacity, snap.PooledCount);
    }

    /// <summary>
    /// §3.3 池级：并发借还期间反复采样 TakeSnapshot，不变量恒定。
    /// <para>
    /// 关键不变量：PooledCount 与 BorrowedCount 恒非负，且二者之和（= 真实存活对象数）
    /// ≤ 池容量；驱逐关闭时不存在「已认领未销毁」瞬态，二者之和精确等于分片登记表存活总数。
    /// 每个 worker 一次只借一个、借完即还，峰值并发持有 ≤ 线程数，永远不把池掏空，
    /// 因此 Acquire 不会走到拒绝策略的超时路径。
    /// </para>
    /// </summary>
    [Fact]
    public void T04_Snapshot_Invariant_HoldsUnderConcurrency()
    {
        const int Capacity = 64;
        const int WorkerRounds = 3000;

        // MinSize=MaxSize=Capacity 预热满池；worker 峰值持有 ≤ 8，永不完全耗尽
        using var pool = BuildQuietPool(minSize: Capacity, maxSize: Capacity);

        var stop = false;
        var unexpected = new ConcurrentBag<Exception>();

        // 采样线程：有界时间片，独立结束，不依赖 worker
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

        // worker：有界轮次，独立结束
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
                    // 理论不会发生（永不完全耗尽）；即便偶发也不判失败，仅跳过
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
    /// 借出态探测策略：OnAcquire 打标、OnRelease 清标、OnDestroy 时若仍带标即为违规。
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
            // 对象仍处借出态却被销毁 —— 说明后台线程破坏了正在使用它的业务线程
            if (_borrowed.ContainsKey(item)) Interlocked.Increment(ref _destroyedWhileBorrowed);
        }
    }

    #endregion

    #region PR-A：P0-新-1 / P1-新-1（overflow / 分片拒绝须走完整 Destroy 触发 OnDestroy）

    /// <summary>
    /// 计数 OnDestroy 的 policy，用于验证「经分片拒绝而销毁的对象」策略钩子必被触发一次。
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
        // T09：登记表已按分片拆分（原池级 _objectMap → 各 Shard 私有字段 _objects），
        // 此处经 _shards 逐分片探测登记表取包装对象。
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard);
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1];
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
    /// P0-新-1：Shard.Add 在分片满（overflow）时不再自行短路处置。
    /// 修复前 overflow 分支会「置 Destroyed=1 + 改终态 Location + 裸 SafeDispose（只 Dispose 不调
    /// _policy.OnDestroy）」，这会让外层完整 Destroy(w) 因幂等 CAS 直接 return，OnDestroy 永不触发。
    /// 修复后 Shard 只拒绝并返回 false，对象的销毁责任完整留给调用方 Destroy(w)。
    /// </summary>
    [Fact]
    public void P0_AddOverflow_RejectsWithoutPrematureDestroy()
    {
        // 单分片、容量 1：塞满后第二个 Add 必然 overflow。
        using var pool = BuildQuietPool(minSize: 0, maxSize: 1);
        var shard = new ShardProxy(pool);

        var w1 = Wrap(1);
        Assert.True(shard.Add(w1));            // 填满分片（None → InPool）

        // 模拟「已借出后归还」的对象（真实 Release overflow 时对象处于 Borrowed）。
        var w2 = Wrap(2);
        SetLocation(w2, /* Borrowed */ 2);
        var accepted = shard.Add(w2);

        // overflow → 拒绝
        Assert.False(accepted);
        // P0-新-1 修复：Shard 不自行标记 Destroyed、不改写 Location 终态
        Assert.Equal(0, GetDestroyedFlag(w2));
        Assert.Equal(/* Borrowed */ 2, GetLocationCode(w2));
        // 该分片仍只有 w1
        Assert.Equal(1, shard.Count);
    }

    /// <summary>
    /// P0-新-1 池级端到端：一个对象被驱逐 / 空闲校验认领（Location=Removing）后，
    /// 业务线程仍 Release 它 → Shard.Add 拒绝 → 走 Release 的完整 Destroy(w) →
    /// OnDestroy 恰触发一次。修复前该对象在 overflow 分支被裸 Dispose 短路，OnDestroy 不会触发。
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

        var item = pool.Acquire();                      // 预填 1 个，Location=Borrowed，位于分片登记表
        var w = GetWrapped(pool, item);
        Assert.Equal(0, policy.OnDestroyCount);

        // 模拟驱逐线程已认领该对象（仅改状态，不真正销毁），随后业务线程误 Release。
        SetLocation(w, /* Removing */ 3);
        pool.Release(item);                             // Shard.Add 拒绝 → 完整 Destroy → OnDestroy++

        Assert.Equal(1, policy.OnDestroyCount);         // P0-新-1：OnDestroy 必触发一次
    }

    /// <summary>
    /// P1-新-1：Release 分片拒绝路径在销毁对象后补水位，池不跌破 MinPoolSize。
    /// 修复前该分支缺 ForceScaleUpOneStep，驱逐 / 归还交错时可能跌破最小水位导致冷启动。
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
            .WithScaleUpCooldownSeconds(0)              // 消除扩容冷却，ForceScaleUp 同步立即执行
            .WithScaleUpStep(2)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .Build();

        // 预填 MinPoolSize=4。借 1 → 剩 3 InPool + 1 Borrowed。
        var item = pool.Acquire();
        Assert.True(pool.GetStats().CurrentSize >= 4);

        // 制造分片拒绝：把对象标记为已认领(Removing)，Release 将销毁它。
        var w = GetWrapped(pool, item);
        SetLocation(w, /* Removing */ 3);
        pool.Release(item);                             // → Destroy 1 个 → 池 3 < Min 4 → ForceScaleUp 补

        // P1-新-1：水位补回 ≥ MinPoolSize（ForceScaleUp 同步执行补 2 → 5）
        var current = pool.GetStats().CurrentSize;
        Assert.True(current >= 4, $"分片拒绝销毁后池应补回水位，实际 CurrentSize={current}");
        Assert.Equal(1, policy.OnDestroyCount);
    }

    #endregion
}

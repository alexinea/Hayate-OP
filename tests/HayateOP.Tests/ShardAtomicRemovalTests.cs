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
            _count = t.GetProperty("Count", Flags)!;

            Assert.NotNull(_add);
            Assert.NotNull(_tryTake);
            Assert.NotNull(_remove);
            Assert.NotNull(_count);
        }

        public int Count => (int)_count.GetValue(_shard)!;

        public bool Add(HayateObject<TestObject> w) => (bool)_add.Invoke(_shard, new object[] { w })!;

        public bool TryTake(out HayateObject<TestObject> w)
        {
            var args = new object?[] { null, false };
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
    /// MinSize 设为 0 时 _objectMap 恒为空，ScalingCallback 会立即返回，不会干扰用例。
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
                while (Volatile.Read(ref running))
                {
                    try { m.Invoke(pool, new object?[] { null }); }
                    catch (TargetInvocationException) { /* 回调内部已兜底，忽略 */ }
                    Thread.Yield();
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
}

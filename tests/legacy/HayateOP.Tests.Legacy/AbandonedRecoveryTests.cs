using System.Collections.Concurrent;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// K2 → M4+ — 借出未还对象自动回收（Abandoned 语义）。M4 泄漏面仅取证（LeakDetectedCount /
/// LeakSuspectedCount，不回收）；K2 在 M4 之上增加 opt-in 回收：超过 <c>RemoveAbandonedTimeout</c>
/// 未归还的借出对象在借取路径（<c>RemoveAbandonedOnBorrow</c>）与后台维护路径
/// （<c>RemoveAbandonedOnMaintenance</c>）上被主动销毁（对齐 CHOPIN AbandonedConfig）。
/// 默认配置必须与 M4 完全等价：只计数、绝不回收。
/// </summary>
public class AbandonedRecoveryTests
{
    private sealed class TestObject : IDisposable
    {
        public int DisposeCount;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    /// <summary>记录每次被销毁的值的策略，供测试观测回收。</summary>
    private sealed class TrackingPolicy : IHayateObjectPolicy<TestObject>
    {
        private readonly ConcurrentBag<TestObject> _destroyed = new();

        public IReadOnlyCollection<TestObject> Destroyed => _destroyed;

        public TestObject Create() => new();

        public bool OnRelease(TestObject item) => true;

        public bool Validate(TestObject item) => true;

        public void OnAcquire(TestObject item) { }

        public void OnPassivate(TestObject item) { }

        public void OnDestroy(TestObject item) => _destroyed.Add(item);
    }

    [Fact]
    public void Default_NoOptIn_BorrowedPastTimeout_IsNotReclaimed_AndStillCountsSuspected()
    {
        // 默认仅取证配置（两个开关全关）必须与 M4 等价：超过阈值的借出对象计入 LeakSuspectedCount，但绝不回收。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            "默认仅取证配置必须继续计数疑似泄漏（M4 行为），实际 " + snapshot.LeakSuspectedCount);
        Assert.Equal(0, snapshot.AbandonedRemovedCount);
        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);

        // 对象仍存活于池中——没有任何回收。
        Assert.Equal(1, snapshot.BorrowedCount);

        pool.Release(abandoned);
    }

    [Fact]
    public void RemoveAbandonedOnBorrow_AbandonedObject_IsReclaimed_NotCountedAsLeak()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithLeakDetectionThreshold(TimeSpan.FromSeconds(30))
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        // 借出 1 个对象并滞留超过回收超时。
        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        // 下一次借取在借取路径上执行回收扫描，销毁滞留对象。
        var second = pool.Acquire();

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.AbandonedRemovedCount >= 1,
            "opt-in 借取路径回收必须回收滞留对象，实际 " + snapshot.AbandonedRemovedCount);
        Assert.Equal(snapshot.AbandonedRemovedCount, pool.GetStats().AbandonedRemovedCount);
        Assert.Contains(abandoned, policy.Destroyed);
        Assert.Equal(0, snapshot.LeakSuspectedCount);
        Assert.Equal(0, snapshot.LeakCount);

        pool.Release(second);
    }

    [Fact]
    public void RemoveAbandonedOnBorrow_ObjectNotPastTimeout_IsNotReclaimed()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromSeconds(30))
            .Build();

        var held = pool.Acquire();
        var second = pool.Acquire(); // 执行扫描；held 远未到超时

        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);
        Assert.DoesNotContain(held, policy.Destroyed);
        Assert.Equal(2, pool.TakeSnapshot().BorrowedCount);

        pool.Release(held);
        pool.Release(second);
    }

    [Fact]
    public void RemoveAbandonedOnMaintenance_AbandonedObject_IsReclaimed()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnMaintenance()
            .WithRemoveAbandonedInterval(50)
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();

        // 首次维护扫描在构造后一个周期触发；等待越过超时并经历数轮扫描以确保确定性。
        Thread.Sleep(700);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.AbandonedRemovedCount >= 1,
            "opt-in 维护路径回收必须回收滞留对象，实际 " + snapshot.AbandonedRemovedCount);
        Assert.Contains(abandoned, policy.Destroyed);

        // 被回收的对象不再计入泄漏误报。
        Assert.Equal(0, snapshot.LeakSuspectedCount);
    }

    [Fact]
    public void RemoveAbandonedOnBorrow_ReleasedObject_SurvivesReclamationScan()
    {
        // 正常归还的对象绝不能被回收：Release 后它离开借出链表。
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(8)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        // 两者均在回收超时内归还，因此 sleep 之后没有任何借出对象越过阈值——
        // 借取路径扫描必须发现空借出链表。
        pool.Release(a);
        pool.Release(b);

        Thread.Sleep(250);

        var c = pool.Acquire(); // 触发扫描；a 与 b 均为空闲、非借出 → 绝不能被回收

        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);
        Assert.DoesNotContain(a, policy.Destroyed);
        Assert.DoesNotContain(b, policy.Destroyed);

        pool.Release(c);
    }

    [Fact]
    public void ReleaseAfterReclaim_DoesNotThrow_AndPoolRemainsUsable()
    {
        // 回收是文档化的 opt-in：调用方仍可能持有引用并在之后归还。池必须保持一致性——
        // 迟到的归还按外部对象处理（走既有外部归还语义），池继续可用。
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        var second = pool.Acquire(); // 回收 `abandoned`
        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1);

        // 迟到的调用方归还已回收对象：不抛异常，池继续正常运转。
        pool.Release(abandoned);

        var third = pool.Acquire();
        pool.Release(third);
        pool.Release(second);

        Assert.True(pool.GetStats().TotalAcquired >= 3);
    }

    [Fact]
    public void LogAbandoned_Enabled_ReclamationDoesNotThrow()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .WithLogAbandoned()
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        var second = pool.Acquire(); // 带日志与已捕获租约栈执行回收

        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1);
        pool.Release(second);
    }

    [Fact]
    public void Builder_RemoveAbandonedTimeout_Zero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HayatePoolBuilder<TestObject>().WithRemoveAbandonedTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void Options_RecoveryEnabled_WithZeroTimeout_IsInvalid()
    {
        var options = new HayatePoolOptions
        {
            RemoveAbandonedOnBorrow = true,
            RemoveAbandonedTimeout = TimeSpan.Zero,
        };
        Assert.False(options.IsValid());

        options.RemoveAbandonedTimeout = TimeSpan.FromSeconds(300);
        Assert.True(options.IsValid());
    }

    [Fact]
    public void CopyTo_PreservesAbandonedRecoverySettings()
    {
        var source = new HayatePoolOptions
        {
            RemoveAbandonedOnBorrow = true,
            RemoveAbandonedOnMaintenance = true,
            RemoveAbandonedTimeout = TimeSpan.FromMinutes(2),
            LogAbandoned = true,
            RemoveAbandonedIntervalMs = 1234,
        };

        var copy = source.CopyTo();

        Assert.True(copy.RemoveAbandonedOnBorrow);
        Assert.True(copy.RemoveAbandonedOnMaintenance);
        Assert.Equal(TimeSpan.FromMinutes(2), copy.RemoveAbandonedTimeout);
        Assert.True(copy.LogAbandoned);
        Assert.Equal(1234, copy.RemoveAbandonedIntervalMs);
    }

    [Fact]
    public void ConcurrentReclaim_DoesNotCorruptPool()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedOnMaintenance()
            .WithRemoveAbandonedInterval(50)
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        // 滞留 8 个对象越过超时，同时其他线程持续借取/归还：回收路径（借取 + 维护）在借出链表上竞争，绝不可破坏它。
        var held = new List<TestObject>();
        for (var i = 0; i < 8; i++) held.Add(pool.Acquire());
        Thread.Sleep(200);

        var stop = 0;
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var obj = pool.Acquire();
                pool.Release(obj);
            }
        })).ToArray();

        Thread.Sleep(500);
        Volatile.Write(ref stop, 1);
        Task.WaitAll(workers);

        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1,
            "并发回收应至少回收滞留对象，实际 " + pool.GetStats().AbandonedRemovedCount);
        Assert.True(policy.Destroyed.Count >= 1);

        // 池仍完全可用。
        var final = pool.Acquire();
        pool.Release(final);
    }
}

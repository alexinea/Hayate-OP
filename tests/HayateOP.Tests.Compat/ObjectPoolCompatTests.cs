using DotNetCore.HayateOP;
using DotNetCore.HayateOP.ObjectPoolCompat;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Diagnostics;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Compat;

/// <summary>
/// T15 验收：现有 Microsoft.Extensions.ObjectPool 调用方零代码改动切换 HayateOP。
/// <para>
/// 同一组消费代码在 DefaultObjectPoolProvider（MEOP 基线）与
/// HayateObjectPoolCompatProvider（HayateOP 后端）上跑出一致结果：
/// 懒创建 / 复用同一实例 / Return=false 丢弃并重建。
/// </para>
/// </summary>
public class ObjectPoolCompatTests
{
    private sealed class CompatResource
    {
        public bool Broken { get; set; }
        public int Used { get; set; }
    }

    /// <summary>计数策略：Return=false 表示丢弃（对齐 MEOP 语义）。</summary>
    private sealed class CountingPolicy : PooledObjectPolicy<CompatResource>
    {
        public int Created;
        public int Returned;
        public override CompatResource Create()
        {
            Interlocked.Increment(ref Created);
            return new CompatResource();
        }
        public override bool Return(CompatResource obj)
        {
            Interlocked.Increment(ref Returned);
            return !obj.Broken;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1. MEOP 基线：同一消费代码的黄金行为
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultObjectPoolProvider_ParityBaseline()
        => RunParityScenario(new DefaultObjectPoolProvider());

    // ─────────────────────────────────────────────────────────────
    // 2. HayateOP 后端跑同一消费代码 → 行为一致（零代码改动切换）
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_MatchesMeopParity()
        => RunParityScenario(new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)   // 冷启动延迟受 L6 行为影响，压短测试时长
        }));

    /// <summary>
    /// 共享消费代码：懒创建 → 归还复用 → 损坏对象丢弃重建。
    /// MEOP 与 HayateOP 后端都必须给出相同结果。
    /// </summary>
    private static void RunParityScenario(ObjectPoolProvider provider)
    {
        var policy = new CountingPolicy();
        ObjectPool<CompatResource> pool = provider.Create(policy);

        // 懒创建：首个 Get 触发一次 Create
        var first = pool.Get();
        Assert.NotNull(first);
        Assert.Equal(1, policy.Created);

        // 归还被接受 → 再次 Get 复用同一实例，不新建
        pool.Return(first);
        Assert.Equal(1, policy.Returned);
        var second = pool.Get();
        Assert.Same(first, second);
        Assert.Equal(1, policy.Created);

        // 损坏对象：Return 策略返回 false → 丢弃；下次 Get 必须新建
        second.Broken = true;
        pool.Return(second);
        Assert.Equal(2, policy.Returned);
        var third = pool.Get();
        Assert.NotSame(second, third);
        Assert.Equal(2, policy.Created);
        Assert.False(third.Broken);
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 冷启动延迟有界（CreateNew 等满 AcquireTimeout 后创建，L6 行为守卫）
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_ColdStartLatencyBounded()
    {
        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)
        });
        var pool = provider.Create(new DefaultPooledObjectPolicy<CompatResource>());

        var sw = Stopwatch.StartNew();
        var obj = pool.Get();
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"cold Get took {sw.ElapsedMilliseconds}ms — CreateNew should create right after AcquireTimeout(50ms)");

        pool.Return(obj);
        pool.Get(); // 归还后立即可复用，不再触发冷启动路径
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 适配器直接包装既有 HayateOP 池（不走 provider 构建路径）
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Adapter_WrapsExistingHayatePool()
    {
        using var hayate = new HayatePoolBuilder<CompatResource>()
            .WithPoolName("compat-direct")
            .WithMinSize(2)
            .WithMaxSize(5)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();

        ObjectPool<CompatResource> pool = new HayateObjectPoolAdapter<CompatResource>(hayate);

        var a = pool.Get();
        pool.Return(a);
        var b = pool.Get();
        Assert.Same(a, b);   // Min=2 预热 + 无驱逐 → 归还后立即取回同一实例

        // 底层池能力透传（GetStats 仍可用）
        Assert.True(hayate.GetStats().TotalAcquired >= 2);
    }

    // ─────────────────────────────────────────────────────────────
    // 5. MEOP 默认策略路径：Create<T>()（new() 约束 → DefaultPooledObjectPolicy）
    // ─────────────────────────────────────────────────────────────

    private sealed class NewableResource
    {
        public int Value { get; set; }
    }

    [Fact]
    public void HayateCompatProvider_DefaultPolicy_CreateT()
    {
        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)
        });

        ObjectPool<NewableResource> pool = provider.Create<NewableResource>();

        var a = pool.Get();
        Assert.NotNull(a);
        a.Value = 42;
        pool.Return(a);
        var b = pool.Get();
        Assert.Same(a, b);
        Assert.Equal(42, b.Value);   // 归还复用，状态保留（MEOP DefaultPooledObjectPolicy 不重置自定义字段）
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Explicit warm-up — PreWarm(count).
/// Acceptance: the call is a floor on <i>idle</i> objects (objects lent out do not count towards it), it is
/// idempotent, it never takes a pool past its own ceiling, it reaches every pool model that retains idle
/// objects, and it stays honest about what it does not promise — it creates objects rather than retaining
/// them, it changes no configuration, and on a preparation pool it does not skip the readiness chain.
/// </summary>
public class PoolPreWarmTests
{
    /// <summary>Hands out distinguishable instances and counts how many were actually created.</summary>
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    private sealed class CountingPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _next;

        public int Created { get; private set; }
        public bool FailCreation { get; set; }

        public TestObject Create()
        {
            if (FailCreation) throw new InvalidOperationException("the dependency is down");

            Created++;
            return new TestObject { Id = ++_next };
        }

        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }

    private sealed class AlwaysReadyStrategy : IHayatePreparationStrategy<TestObject>
    {
        public int ReadyCalls { get; private set; }

        public Task<bool> IsReadyAsync(TestObject item, CancellationToken cancellationToken = default)
        {
            ReadyCalls++;
            return Task.FromResult(true);
        }

        public Task PrepareAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Builds a pool whose idle set only ever changes through the calls under test: nothing is pre-created,
    /// and the background eviction and the auto-scaler are off, so the ceiling stays exactly where the case
    /// put it. A per-shard capacity of <c>MaxPoolSize / ShardCount</c> floors to zero with the default four
    /// shards, which is why every small pool in this file is built single-sharded.
    /// </summary>
    private static IHayateObjectPool<TestObject> BuildPool(
        CountingPolicy policy, int maxSize, bool lean = false, int creationRetries = 3)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithMinSize(0)
            .WithMaxSize(maxSize)
            .WithCreationRetryCount(creationRetries);

        if (lean) return builder.WithLean().Build();

        return builder
            .WithShardCount(1)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();
    }

    [Fact]
    public void PreWarm_ShouldCreateUntilThePoolHoldsThatManyIdleObjects()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 8);

        Assert.Equal(4, pool.PreWarm(4));
        Assert.Equal(4, policy.Created);

        // The warmed objects are really there: four borrows create nothing and hand out four distinct ones.
        var borrowed = Enumerable.Range(0, 4).Select(_ => pool.Acquire()).ToArray();
        Assert.Equal(4, policy.Created);
        Assert.Equal(4, borrowed.Select(o => o.Id).Distinct().Count());

        foreach (var item in borrowed) pool.Release(item);
    }

    [Fact]
    public void PreWarm_ShouldNotCreateAnythingWhenTheIdleSetAlreadyCoversTheCount()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 8);

        Assert.Equal(4, pool.PreWarm(4));
        Assert.Equal(0, pool.PreWarm(4));   // the floor is already met
        Assert.Equal(0, pool.PreWarm(2));   // a lower floor is met as well
        Assert.Equal(4, policy.Created);
    }

    [Fact]
    public void PreWarm_ShouldCountOnlyIdleObjects()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 8);

        Assert.Equal(4, pool.PreWarm(4));

        var first = pool.Acquire();
        var second = pool.Acquire();

        // Borrowed objects do not count towards the floor, so the shortfall is created again.
        Assert.Equal(2, pool.PreWarm(4));
        Assert.Equal(6, policy.Created);

        // Once they are back, the floor is met without creating anything.
        pool.Release(first);
        pool.Release(second);
        Assert.Equal(0, pool.PreWarm(4));
        Assert.Equal(6, policy.Created);
    }

    [Fact]
    public void PreWarm_ShouldWarmToTheCeilingRatherThanFail()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 4);

        // The request is above the pool's own ceiling: it warms up to the ceiling instead of throwing or
        // creating past it. The ceiling below is structural — a shard refuses more than its share — so this
        // case pins the contract (no throw, no runaway creation) rather than one particular clamp.
        Assert.Equal(4, pool.PreWarm(10));
        Assert.Equal(0, pool.PreWarm(10));
        Assert.Equal(4, policy.Created);
    }

    [Fact]
    public void PreWarm_ShouldRejectANegativeCount()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => { pool.PreWarm(-1); });
    }

    [Fact]
    public void PreWarm_ShouldReportAFailedCreationInsteadOfSwallowingIt()
    {
        var policy = new CountingPolicy { FailCreation = true };
        using var pool = BuildPool(policy, maxSize: 4, creationRetries: 1);

        // The construction-time warm-up logs and carries on, because the constructor cannot fail; an
        // explicit request is the caller's decision and reports the failure.
        Assert.Throws<InvalidOperationException>(() => { pool.PreWarm(2); });
    }

    [Fact]
    public void PreWarm_ShouldWarmTheLeanBuffer()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 4, lean: true);

        Assert.Equal(3, pool.PreWarm(3));
        Assert.Equal(3, policy.Created);

        // The borrows are served from the buffer — nothing is created for them.
        var borrowed = Enumerable.Range(0, 3).Select(_ => pool.Acquire()).ToArray();
        Assert.Equal(3, policy.Created);

        foreach (var item in borrowed) pool.Release(item);
        Assert.Equal(0, pool.PreWarm(3));
    }

    [Fact]
    public void PreWarm_ShouldWarmTheUnboundedPool()
    {
        var created = 0;
        var pool = new HayateUnboundedPool<TestObject>(maxIdle: 8, () =>
        {
            created++;
            return new TestObject();
        });

        using (pool)
        {
            Assert.Equal(3, pool.PreWarm(3));
            Assert.Equal(3, pool.PooledCount);

            // The resident set is this model's ceiling: a request above it warms up to the ceiling, not past
            // it. The request is deliberately larger than the ceiling so that "up to it" is a claim with a
            // visible difference rather than a coincidence.
            Assert.Equal(5, pool.PreWarm(20));
            Assert.Equal(8, pool.PooledCount);
            Assert.Equal(0, pool.PreWarm(20));
            Assert.Equal(8, created);

            // The warmed objects serve the borrows — this model creates only on a miss.
            var borrowed = Enumerable.Range(0, 8).Select(_ => pool.Acquire()).ToArray();
            Assert.Equal(0, pool.PooledCount);
            Assert.Equal(8, created);

            var extra = pool.Acquire();
            Assert.Equal(9, created);

            // Returning more than the retained set allows parks up to the limit and drops the rest, so the
            // floor is met again without creating anything.
            foreach (var item in borrowed) pool.Release(item);
            pool.Release(extra);
            Assert.Equal(8, pool.PooledCount);
            Assert.Equal(0, pool.PreWarm(20));
        }
    }

    [Fact]
    public void PreWarm_ShouldReachTheInnerPoolThroughThePreparationDecorator()
    {
        var policy = new CountingPolicy();
        var inner = BuildPool(policy, maxSize: 8);
        using var pool = inner.WithPreparation(new AlwaysReadyStrategy());

        Assert.Equal(2, pool.PreWarm(2));
        Assert.Equal(2, policy.Created);
        Assert.Equal(0, pool.PreWarm(2));
    }

    [Fact]
    public async Task PreWarm_ShouldNotSkipThePreparationChain()
    {
        var policy = new CountingPolicy();
        var strategy = new AlwaysReadyStrategy();
        var inner = BuildPool(policy, maxSize: 8);
        using var pool = inner.WithPreparation(strategy);

        Assert.Equal(2, pool.PreWarm(2));

        // Warming creates objects, it does not prepare them: every borrow still runs the readiness check.
        Assert.Equal(0, strategy.ReadyCalls);

        await pool.AcquireAsync();
        await pool.AcquireAsync();
        Assert.Equal(2, strategy.ReadyCalls);
    }

    [Fact]
    public async Task PreWarm_ShouldStayWithinTheCeilingUnderConcurrentCalls()
    {
        const int ceiling = 16;
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: ceiling);

        var warmers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => pool.PreWarm(ceiling)));
        var warmed = await Task.WhenAll(warmers);

        // Every caller is served from the shared result; the pool ends up exactly at its ceiling.
        Assert.Equal(ceiling, pool.GetStats().PooledCount);
        Assert.True(warmed.Sum() >= ceiling, "each object is created by exactly one of the concurrent warm-ups");

        var afterWarm = policy.Created;
        var borrowed = Enumerable.Range(0, ceiling).Select(_ => pool.Acquire()).ToArray();

        // Sixteen borrows served from the warmed set: no racing warm-up created past the ceiling.
        Assert.Equal(afterWarm, policy.Created);

        foreach (var item in borrowed) pool.Release(item);
    }

    [Fact]
    public void PreWarm_ShouldNotChangeTheConfigurationItWarms()
    {
        var policy = new CountingPolicy();
        using var pool = BuildPool(policy, maxSize: 8);

        pool.PreWarm(6);

        // Warming is a one-off request, not a new minimum: the options are exactly as they were, so the
        // extra objects age like any other idle object.
        var options = pool.GetOptions();
        Assert.Equal(0, options.MinPoolSize);
        Assert.Equal(8, options.MaxPoolSize);
    }

    [Fact]
    public void PreWarm_ShouldReachTheSpecializedPools()
    {
        using var pool = new StringBuilderPool(maxPoolSize: 8);

        Assert.Equal(4, pool.PreWarm(4));
        Assert.Equal(0, pool.PreWarm(4));
    }
}

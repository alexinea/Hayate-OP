using System;
using System.Threading;
using DotNetCore.HayateOP.Common;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Shared pools (O-C): the process-wide catalog that hands every caller the same pool for an element
/// type, built once and addressed through the same registry the rest of the library uses.
/// </summary>
public class SharedPoolTests
{
    private sealed class TestObject { }

    private sealed class OtherObject { }

    private sealed class ProcessWideProbe { }

    private sealed class TrackedProbe : IDisposable
    {
        public static int Created;
        public static int Disposed;

        public TrackedProbe() => Interlocked.Increment(ref Created);

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public void GetOrCreate_ShouldReturnTheSameInstanceOnEveryCall()
    {
        using var catalog = new HayateSharedPoolRegistry();

        var first = catalog.GetOrCreate<TestObject>();
        var second = catalog.GetOrCreate<TestObject>();

        Assert.Same(first, second);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void GetOrCreate_ShouldKeepOnePoolPerElementType()
    {
        using var catalog = new HayateSharedPoolRegistry();

        var a = catalog.GetOrCreate<TestObject>();
        var b = catalog.GetOrCreate<OtherObject>();

        Assert.NotSame(a, b);
        Assert.Equal(2, catalog.Count);
        Assert.Same(a, catalog.GetOrCreate<TestObject>());
        Assert.Same(b, catalog.GetOrCreate<OtherObject>());
    }

    [Fact]
    public void GetOrCreate_ShouldApplyTheConfigurationOnlyToTheCallThatCreatesThePool()
    {
        using var catalog = new HayateSharedPoolRegistry();
        var callbacks = 0;

        var first = catalog.GetOrCreate<TestObject>(o => { callbacks++; o.ShardCount = 1; o.MinPoolSize = 1; o.MaxPoolSize = 4; });
        var second = catalog.GetOrCreate<TestObject>(o => { callbacks++; o.MaxPoolSize = 999; });

        Assert.Same(first, second);
        // The second callback never runs: the shared pool is configured once, by whoever creates it.
        Assert.Equal(1, callbacks);
        Assert.Equal(4, second.GetOptions().MaxPoolSize);
        Assert.Equal(1, second.GetOptions().MinPoolSize);
    }

    [Fact]
    public void GetOrCreate_ShouldKeepTheOptionsOfEachElementTypeIndependent()
    {
        using var catalog = new HayateSharedPoolRegistry();

        catalog.GetOrCreate<TestObject>(o => { o.ShardCount = 1; o.MinPoolSize = 1; o.MaxPoolSize = 4; });
        var defaulted = catalog.GetOrCreate<OtherObject>();

        Assert.Equal(4, catalog.GetOrCreate<TestObject>().GetOptions().MaxPoolSize);
        Assert.Equal(HayateConstant.DEFAULT_MAX_POOL_SIZE, defaulted.GetOptions().MaxPoolSize);
        Assert.Equal(HayateConstant.DEFAULT_MIN_POOL_SIZE, defaulted.GetOptions().MinPoolSize);
    }

    [Fact]
    public void GetOrCreateNamed_ShouldAllowSeveralNamedPoolsPerElementType()
    {
        using var catalog = new HayateSharedPoolRegistry();

        var primary = catalog.GetOrCreateNamed<TestObject>("primary");
        var replica = catalog.GetOrCreateNamed<TestObject>("replica");

        Assert.NotSame(primary, replica);
        Assert.Equal(2, catalog.Count);
        Assert.Same(primary, catalog.GetOrCreateNamed<TestObject>("primary"));
        Assert.Same(replica, catalog.GetOrCreateNamed<TestObject>("replica"));
    }

    [Fact]
    public void GetOrCreate_ShouldTreatTheDefaultNameAsTheUnnamedPool()
    {
        using var catalog = new HayateSharedPoolRegistry();

        var unnamed = catalog.GetOrCreate<TestObject>();
        var named = catalog.GetOrCreateNamed<TestObject>(HayateSharedPoolRegistry.DefaultSharedName);

        Assert.Same(unnamed, named);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void TryGet_ShouldNeverCreateThePool()
    {
        using var catalog = new HayateSharedPoolRegistry();

        Assert.False(catalog.TryGet<TestObject>(out var missing));
        Assert.Null(missing);
        Assert.Equal(0, catalog.Count);

        var created = catalog.GetOrCreate<TestObject>();

        Assert.True(catalog.TryGet<TestObject>(out var found));
        Assert.Same(created, found);
    }

    [Fact]
    public void TryGet_ShouldReportAnEntryOfAnotherElementTypeAsAbsent()
    {
        using var catalog = new HayateSharedPoolRegistry();
        using var foreign = new HayatePoolBuilder<OtherObject>().Build();

        // Occupy the canonical name of TestObject with a pool of a different element type: the lookup
        // must report it absent rather than hand back a pool the caller cannot use.
        catalog.Registry.Register(HayateServiceKey.Create<TestObject>("shared").RegistryName, foreign);

        Assert.False(catalog.TryGet<TestObject>(out var mismatch));
        Assert.Null(mismatch);
    }

    [Fact]
    public void TryGet_ShouldReportUnusableNamesAsAbsent()
    {
        using var catalog = new HayateSharedPoolRegistry();
        catalog.GetOrCreate<TestObject>();

        Assert.False(catalog.TryGet<TestObject>(null!, out _));
        Assert.False(catalog.TryGet<TestObject>("   ", out _));
        Assert.False(catalog.TryGet<TestObject>("a:b", out _));
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Remove_ShouldDisposeThePoolAndAllowRecreation()
    {
        using var catalog = new HayateSharedPoolRegistry();
        var pool = catalog.GetOrCreate<TrackedProbe>(o =>
        {
            o.ShardCount = 1;
            o.MinPoolSize = 0;
            o.MaxPoolSize = 2;
            o.RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand;
        });

        // One idle object, so disposing the pool is observable through the object's own Dispose.
        Assert.Equal(1, pool.PreWarm(1));
        var disposedBefore = Volatile.Read(ref TrackedProbe.Disposed);

        Assert.True(catalog.Remove<TrackedProbe>());

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.TryGet<TrackedProbe>(out _));
        // Removing disposes: the catalog owns its pools, and the idle object went with the pool.
        Assert.Equal(disposedBefore + 1, Volatile.Read(ref TrackedProbe.Disposed));

        var recreated = catalog.GetOrCreate<TrackedProbe>();
        Assert.NotSame(pool, recreated);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Remove_ShouldRefuseNamesThatAreNotHeld()
    {
        using var catalog = new HayateSharedPoolRegistry();

        Assert.False(catalog.Remove<TestObject>());
        Assert.False(catalog.Remove<TestObject>("   "));
        Assert.False(catalog.Remove<TestObject>("a:b"));
        Assert.False(catalog.Remove<TestObject>(null!));

        catalog.GetOrCreate<TestObject>();

        // A name held for another element type is not this element type's entry.
        Assert.False(catalog.Remove<OtherObject>());
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Clear_ShouldReleaseOwnPoolsOnly()
    {
        var registry = new HayateObjectPoolRegistry();
        // A pool the catalog never created, holding an object the catalog must not destroy.
        using var foreign = new HayatePoolBuilder<TrackedProbe>()
            .WithPoolName("foreign")
            .WithShardCount(1)
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();
        Assert.Equal(1, foreign.PreWarm(1));
        registry.Register("foreign", foreign);

        using var catalog = new HayateSharedPoolRegistry(registry);
        var own = catalog.GetOrCreate<TrackedProbe>(o => { o.ShardCount = 1; o.MinPoolSize = 0; o.MaxPoolSize = 2; });

        Assert.Equal(1, own.PreWarm(1));
        Assert.Equal(2, catalog.Count);
        var disposedBefore = Volatile.Read(ref TrackedProbe.Disposed);

        catalog.Clear();

        // Exactly one pooled object was destroyed — the catalog's own. A catalog that released every
        // registration would take the foreign pool's object with it and report two.
        Assert.Equal(disposedBefore + 1, Volatile.Read(ref TrackedProbe.Disposed));
        Assert.Equal(1, catalog.Count);
        Assert.False(catalog.TryGet<TrackedProbe>(out _));

        // The catalog never claims what it did not create: the foreign pool is still registered and alive.
        Assert.True(registry.TryGet("foreign", out var stillThere));
        Assert.Same(foreign, stillThere);
        using (var lease = foreign.AcquireScoped())
        {
            Assert.NotNull(lease.Value);
        }
    }

    [Fact]
    public void Dispose_ShouldEmptyTheCatalogAndRejectFurtherCreation()
    {
        var catalog = new HayateSharedPoolRegistry();
        catalog.GetOrCreate<TestObject>();

        catalog.Dispose();

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.TryGet<TestObject>(out _));
        Assert.Throws<ObjectDisposedException>(() => catalog.GetOrCreate<TestObject>());

        // Idempotent.
        catalog.Dispose();
    }

    [Fact]
    public void Registry_ShouldExposeTheSharedPoolsForManagementEnumeration()
    {
        using var catalog = new HayateSharedPoolRegistry();

        var pool = catalog.GetOrCreateNamed<TestObject>("primary");

        var entry = Assert.Single(catalog.Registry.GetAll());
        Assert.Equal(HayateServiceKey.Create<TestObject>("primary").RegistryName, entry.PoolName);
        Assert.Equal(typeof(TestObject), entry.ElementType);
        Assert.Same(pool, entry.Pool);
        Assert.Equal(1, catalog.Registry.Count);
    }

    [Fact]
    public void GetOrCreate_ShouldBuildExactlyOnePoolUnderConcurrentFirstCalls()
    {
        using var catalog = new HayateSharedPoolRegistry();
        var results = new IHayateObjectPool<TestObject>?[8];

        // Release every thread at once so they all reach the "does it exist yet?" check before any of
        // them has finished building, which is the only window in which a second pool could be built.
        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[results.Length];
        for (var i = 0; i < threads.Length; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                start.Wait();
                results[index] = catalog.GetOrCreate<TestObject>();
            });
            threads[i].Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        }

        Assert.All(results, pool => Assert.Same(results[0], pool));
        // The winner is the one that stayed in the catalog, so a later caller resolves the same instance.
        Assert.Same(results[0], catalog.GetOrCreate<TestObject>());
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void GetOrCreateNamed_ShouldRejectNamesThatCannotBeAddressed()
    {
        using var catalog = new HayateSharedPoolRegistry();

        Assert.Throws<ArgumentNullException>(() => catalog.GetOrCreateNamed<TestObject>(null!));
        Assert.Throws<ArgumentException>(() => catalog.GetOrCreateNamed<TestObject>("   "));
        Assert.Throws<ArgumentException>(() => catalog.GetOrCreateNamed<TestObject>("a:b"));
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void GetOrCreate_ShouldAcceptAnAbsentConfigurationCallback()
    {
        using var catalog = new HayateSharedPoolRegistry();

        // A null callback means "no configuration", the same pool GetOrCreate<T>() returns — this is the
        // overload a bare null argument binds to, which is why the named path is a separate method.
        var withNull = catalog.GetOrCreate<TestObject>((Action<HayatePoolOptions>?)null);

        Assert.Same(withNull, catalog.GetOrCreate<TestObject>());
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void SharedPool_ShouldServeBorrowAndReleaseLikeAnyOtherPool()
    {
        using var catalog = new HayateSharedPoolRegistry();
        var pool = catalog.GetOrCreate<TrackedProbe>(o =>
        {
            o.ShardCount = 1;
            o.MinPoolSize = 0;
            o.MaxPoolSize = 2;
            o.RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand;
        });

        var before = Volatile.Read(ref TrackedProbe.Created);
        var a = pool.Acquire();
        var b = pool.Acquire();
        Assert.Equal(before + 2, Volatile.Read(ref TrackedProbe.Created));

        pool.Release(a);
        pool.Release(b);

        var c = pool.Acquire();
        // The returned objects were pooled, not recreated.
        Assert.Equal(before + 2, Volatile.Read(ref TrackedProbe.Created));
        Assert.True(ReferenceEquals(c, a) || ReferenceEquals(c, b));
        pool.Release(c);
    }

    [Fact]
    public void Shared_ShouldHandOutTheProcessWidePool()
    {
        try
        {
            var fromEntryPoint = HayatePool.Shared<ProcessWideProbe>();
            var fromCatalog = HayateSharedPoolRegistry.Default.GetOrCreate<ProcessWideProbe>();
            var again = HayatePool.Shared<ProcessWideProbe>(o => o.MaxPoolSize = 123);

            Assert.Same(fromEntryPoint, fromCatalog);
            Assert.Same(fromEntryPoint, again);
            Assert.Equal(HayateConstant.DEFAULT_MAX_POOL_SIZE, again.GetOptions().MaxPoolSize);
        }
        finally
        {
            // The process-wide catalog outlives the test, so hand the entry back.
            Assert.True(HayateSharedPoolRegistry.Default.Remove<ProcessWideProbe>());
        }
    }
}

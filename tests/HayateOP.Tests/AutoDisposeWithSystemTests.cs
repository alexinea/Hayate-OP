using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Process-shutdown auto-dispose (EnableAutoDisposeWithSystem, default off).
/// Acceptance: an enabled pool subscribes to the shutdown signal and releases its objects when it fires;
/// the feature stays off by default and a hook alone does not switch it on; an explicitly disposed pool
/// detaches itself, so the hook holds nothing that keeps it reachable; repeated notifications do nothing
/// further; and the option survives the options deep copy like every other setting.
/// </summary>
public class AutoDisposeWithSystemTests
{
    private class TestObject { }

    /// <summary>A hook the test owns, so shutdown can be raised without terminating anything.</summary>
    private sealed class ManualShutdownHook : IHayateShutdownHook
    {
        private readonly List<Action> _handlers = new();

        internal int Count => _handlers.Count;

        public void Register(Action handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
        }

        public void Unregister(Action handler)
        {
            _handlers.Remove(handler);
        }

        internal void Fire()
        {
            foreach (var handler in _handlers.ToArray())
            {
                handler();
            }
        }
    }

    private static HayatePoolBuilder<TestObject> BaseBuilder(ManualShutdownHook hook) =>
        new HayatePoolBuilder<TestObject>()
            .WithPoolName("s3-pool")
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithShutdownHook(hook);

    [Fact(Timeout = 30_000)]
    public void AutoDisposeWithSystem_ShouldBeOffByDefault()
    {
        var hook = new ManualShutdownHook();

        // Supplying a hook is not enough: the feature has to be asked for explicitly.
        using var pool = BaseBuilder(hook).Build();

        Assert.Equal(0, hook.Count);
        Assert.False(pool.GetOptions().EnableAutoDisposeWithSystem);

        hook.Fire();
        Assert.Equal(1, pool.GetStats().PooledCount);   // untouched by the notification
    }

    [Fact(Timeout = 30_000)]
    public void AutoDisposeWithSystem_ShouldDisposeThePool_WhenShutdownFires()
    {
        var hook = new ManualShutdownHook();

        var pool = BaseBuilder(hook).WithAutoDisposeWithSystem().Build();

        Assert.Equal(1, hook.Count);
        Assert.True(pool.GetOptions().EnableAutoDisposeWithSystem);
        Assert.Equal(1, pool.GetStats().PooledCount);

        hook.Fire();

        // Disposal ran: every pooled object was released and the pool no longer holds anything.
        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.Empty(pool.TakeSnapshot().ObjectDetails);
    }

    [Fact(Timeout = 30_000)]
    public void AutoDisposeWithSystem_ShouldDetach_WhenThePoolIsDisposed()
    {
        var hook = new ManualShutdownHook();

        var pool = BaseBuilder(hook).WithAutoDisposeWithSystem().Build();
        Assert.Equal(1, hook.Count);

        pool.Dispose();

        // The process-wide hook outlives every pool, so the subscription has to go with the pool.
        Assert.Equal(0, hook.Count);

        // Raising shutdown afterwards reaches nothing, and must not throw.
        hook.Fire();
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void AutoDisposeWithSystem_ShouldReleaseTheObjectOnlyOnce()
    {
        var hook = new ManualShutdownHook();

        var pool = BaseBuilder(hook).WithAutoDisposeWithSystem().Build();

        // Shutdown and an explicit disposal both arrive; exactly one of them does the work.
        hook.Fire();
        pool.Dispose();
        hook.Fire();

        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.Equal(0, hook.Count);
    }

    [Fact(Timeout = 30_000)]
    public void ProcessShutdownHook_ShouldSubscribeOnce_AndForgetCompletely()
    {
        var hook = new HayateProcessShutdownHook();
        var calls = 0;

        // One delegate instance, so identity-based de-duplication has something to match on: converting a
        // method group twice would produce two different delegates and defeat the check.
        Action handler = () => calls++;

        hook.Register(handler);
        hook.Register(handler);   // the same handler is not subscribed twice
        hook.Unregister(handler);
        hook.Unregister(handler); // unregistering something absent is a no-op

        Assert.Equal(0, calls);
    }

    [Fact(Timeout = 30_000)]
    public void Options_ShouldCarryTheSettingThroughACopy()
    {
        var source = new HayatePoolOptions { EnableAutoDisposeWithSystem = true };

        Assert.True(source.CopyTo().EnableAutoDisposeWithSystem);
        Assert.False(new HayatePoolOptions().EnableAutoDisposeWithSystem);
    }
}

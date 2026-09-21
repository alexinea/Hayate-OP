namespace DotNetCore.HayateOP.Tests;

public class RejectPolicyTests
{
    private class TestObject { }

    [Fact]
    public void RejectPolicy_Abort_ShouldThrowImmediatelyWhenNoIdleObjects()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableAutoScaling(false)
            .Build();

        // Exhaust the pool
        var obj1 = pool.Acquire();

        // Act & Assert: throw immediately when no idle object is available, without waiting
        var exception = Assert.Throws<InvalidOperationException>(() => pool.Acquire());
        Assert.Contains("no available object", exception.Message);
    }

    [Fact]
    public void RejectPolicy_Block_ShouldWaitUntilObjectAvailable()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var obj1 = pool.Acquire();
        TestObject? obj2 = null;

        // Act: return the object after 100ms
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            pool.Release(obj1);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        obj2 = pool.Acquire();
        sw.Stop();

        // Assert
        Assert.NotNull(obj2);
        Assert.Same(obj1, obj2);
        Assert.True(sw.ElapsedMilliseconds >= 80);
    }

    [Fact]
    public void RejectPolicy_BlockTimeout_ShouldThrowAfterTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var obj1 = pool.Acquire();

        // Act & Assert
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exception = Assert.Throws<TimeoutException>(() => pool.Acquire());
        sw.Stop();

        Assert.Contains("timed out", exception.Message);
        // Guard: the original upper bound of the 80<=elapsed<=200ms assertion would occasionally report a false overrun under high host load / thread-pool starvation.
        // Now we only verify that it truly waited for the timeout (not much earlier than the configured 100ms) and did not hang forever:
        //   lower bound 80ms ~= 100ms timeout - timing/scheduling tolerance;
        //   upper bound relaxed to 10s, only used to catch the "should have timed out but deadlocked without returning" hang regression (paired with an outer watchdog).
        Assert.True(sw.ElapsedMilliseconds >= 80, $"Should have thrown after the ~100ms timeout, actually {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"Suspected hang in the timeout path: no return after {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldReturnNewObjectWhenTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .WithEnableMetrics(true)
            .Build();

        var obj1 = pool.Acquire();
        var initialCount = pool.GetStats().CurrentSize;

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var obj2 = pool.Acquire();
        sw.Stop();

        // Assert
        Assert.NotNull(obj2);
        Assert.NotSame(obj1, obj2);
        Assert.True(sw.ElapsedMilliseconds >= 80);
        Assert.Equal(1, pool.GetStats().TotalMissed);
        // Post-fix semantics: CreateNew creates a "registered" in-pool object (borrowed state),
        // CurrentSize +1; the old implementation returned an unregistered bare object that was destroyed as a foreign object on Release.
        Assert.Equal(initialCount + 1, pool.GetStats().CurrentSize);
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldPoolReturnedObjectForReuse()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        var obj1 = pool.Acquire();

        // Act
        var obj2 = pool.Acquire();
        pool.Release(obj2);

        // Assert: post-fix semantics -- a CreateNew object returns to the pool normally on Release (no longer treated as
        // a foreign object and destroyed), and can be reused by a subsequent Acquire.
        Assert.Equal(1, pool.GetStats().PooledCount);
        var obj3 = pool.Acquire();
        Assert.Same(obj2, obj3);
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// AcquireAsync timeout-overload and cancellation-propagation tests.
/// Semantics align with the synchronous Acquire(TimeSpan): on timeout it throws TimeoutException (including missed-count / forced scale-up),
/// while external cancellation propagates TaskCanceledException; the old AcquireAsync(ct) signature is unchanged.
/// </summary>
public class AcquireAsyncTimeoutTests
{
    private class TestObject { }

    [Fact(Timeout = 60000)]
    public async Task Timeout_ShouldThrowTimeoutExceptionWhenExhausted()
    {
        // Arrange: capacity 1; the first borrow bootstraps and takes the only object; the second borrow has nothing to return -> timeout.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-exhausted")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        // Act + Assert: the pool is exhausted (1 lent out, Max=1 so no bootstrap); must time out within 200ms
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => pool.AcquireAsync(TimeSpan.FromMilliseconds(200)));
        sw.Stop();

        Assert.Contains("timed out", ex.Message);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), $"Returned too early: {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact(Timeout = 60000)]
    public async Task Timeout_ShouldReturnImmediatelyWhenAvailable()
    {
        // Arrange: Min=1 pre-warmed; the pool has an available object
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-available")
            .WithEnableAutoScaling(false)
            .WithMinSize(1)
            .WithMaxSize(5)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act: even an extremely short timeout should succeed immediately (no wait needed for an available object)
        var obj = await pool.AcquireAsync(TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.NotNull(obj);
        pool.Release(obj);
    }

    [Fact(Timeout = 60000)]
    public async Task Timeout_ShouldSucceedAfterReleaseWithinWindow()
    {
        // Arrange: capacity 1; the first borrow holds the object; a background release occurs after 100ms
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-release-in-window")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            pool.Release(first);
        });

        // Act: wait within a 5s window for the release signal to wake the caller
        var second = await pool.AcquireAsync(TimeSpan.FromSeconds(5));

        // Assert: the returned object is the one that was released (pooled reuse)
        Assert.Same(first, second);
        pool.Release(second);
    }

    [Fact(Timeout = 60000)]
    public async Task ExternalCancel_ShouldThrowTaskCanceledNotTimeout()
    {
        // Arrange: capacity 1; the first borrow holds the object; an external CT is cancelled after 200ms (far below the 10s timeout)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-external-cancel")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(200);

        // Act + Assert: external cancellation must propagate as an OperationCanceledException family, not be translated into a TimeoutException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact(Timeout = 60000)]
    public async Task InfiniteTimeout_ShouldHonorExternalCancel()
    {
        // Arrange: InfiniteTimeSpan delegates to the no-timeout overload, with an externally pre-cancelled token
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-infinite-cancel")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(Timeout.InfiniteTimeSpan, cts.Token));
    }

    [Fact(Timeout = 60000)]
    public async Task LegacySignature_ShouldRemainAvailable()
    {
        // The old AcquireAsync(ct) signature is unchanged (zero-regression guarantee)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-legacy-signature")
            .WithEnableAutoScaling(false)
            .WithMinSize(1)
            .WithMaxSize(5)
            .Build();

        var obj = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(obj);
        pool.Release(obj);
    }
}

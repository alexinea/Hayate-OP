using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Tests for the AcquireAsync timeout overload and cancellation propagation.
/// Mirrors the synchronous Acquire(TimeSpan): a timeout throws TimeoutException (with the missed counter / forced expansion),
/// external cancellation propagates a TaskCanceledException; the original AcquireAsync(ct) signature is unchanged.
/// </summary>
public class AcquireAsyncTimeoutTests
{
    private class TestObject { }

    [Fact]
    public async Task Timeout_ShouldThrowTimeoutExceptionWhenExhausted()
    {
        // Arrange: capacity 1, the first borrow bootstraps and takes the only object; the second borrow has nothing to return -> times out.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-exhausted")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        // Act + Assert: the pool is exhausted (1 borrowed, Max=1 so it does not bootstrap), so a 200ms timeout is guaranteed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => pool.AcquireAsync(TimeSpan.FromMilliseconds(200)));
        sw.Stop();

        Assert.Contains("timed out", ex.Message);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), $"Returned too early: {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Timeout_ShouldReturnImmediatelyWhenAvailable()
    {
        // Arrange: Min=1 warm-up, pool has an available object
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-available")
            .WithEnableAutoScaling(false)
            .WithMinSize(1)
            .WithMaxSize(5)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act: even a very short timeout must succeed immediately (an available object needs no wait)
        var obj = await pool.AcquireAsync(TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.NotNull(obj);
        pool.Release(obj);
    }

    [Fact]
    public async Task Timeout_ShouldSucceedAfterReleaseWithinWindow()
    {
        // Arrange: capacity 1, the first borrow occupies it; it is returned in the background after 100ms
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

        // Act: wait within the 5s window for the return signal to wake the call
        var second = await pool.AcquireAsync(TimeSpan.FromSeconds(5));

        // Assert: the retrieved object is the returned one (pooled reuse)
        Assert.Same(first, second);
        pool.Release(second);
    }

    [Fact]
    public async Task ExternalCancel_ShouldThrowTaskCanceledNotTimeout()
    {
        // Arrange: capacity 1, the first borrow occupies it; the external CT is cancelled after 200ms (far less than the 10s timeout)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-external-cancel")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(200);

        // Act + Assert: external cancellation must propagate as the OperationCanceledException family, not be translated into a TimeoutException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task InfiniteTimeout_ShouldHonorExternalCancel()
    {
        // Arrange: InfiniteTimeSpan delegates to the no-timeout overload with an externally pre-cancelled token
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

    [Fact]
    public async Task LegacySignature_ShouldRemainAvailable()
    {
        // The legacy AcquireAsync(ct) signature is unchanged (zero-breakage commitment)
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

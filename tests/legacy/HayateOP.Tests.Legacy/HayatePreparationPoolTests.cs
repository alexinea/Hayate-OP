using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// HayatePreparationPool (N3): the asynchronous preparation/reconnect strategy aligned with
/// marklauter's IPreparationStrategy. Verified: ready objects are delivered without a prepare call,
/// not-ready objects are prepared and delivered, a failed prepare discards the object (onDiscard)
/// and retries with another, the retry budget ends in HayatePoolPreparationException with the last
/// failure attached, cancellation propagates, every other member delegates to the inner pool, and
/// the wrapper composes with the bounded engine and the unbounded pool.
/// </summary>
public class HayatePreparationPoolTests
{
    private sealed class Connection : IDisposable
    {
        public bool Connected { get; set; }
        public bool Disposed { get; set; }
        public bool BeyondRepair { get; set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Strategy : IHayatePreparationStrategy<Connection>
    {
        public Func<Connection, bool> ReadyCheck { get; set; } = static _ => true;
        public Func<Connection, CancellationToken, Task>? Prepare { get; set; }
        public int ReadyCalls { get; private set; }
        public int PrepareCalls { get; private set; }

        public Task<bool> IsReadyAsync(Connection item, CancellationToken cancellationToken = default)
        {
            ReadyCalls++;
            return Task.FromResult(ReadyCheck(item));
        }

        public Task PrepareAsync(Connection item, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            if (Prepare is null)
            {
                item.Connected = true;
                return Task.CompletedTask;
            }

            return Prepare(item, cancellationToken);
        }
    }

    private sealed class ReconnectingStrategy : IHayatePreparationStrategy<Connection>
    {
        public Func<Connection, CancellationToken, Task> OnPrepare { get; set; } = static (_, _) => Task.CompletedTask;
        public int PrepareCalls { get; private set; }

        public Task<bool> IsReadyAsync(Connection item, CancellationToken cancellationToken = default)
            => Task.FromResult(item.Connected);

        public Task PrepareAsync(Connection item, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return OnPrepare(item, cancellationToken);
        }
    }

    [Fact]
    public async Task ReadyObject_ShouldBeDeliveredWithoutPrepare()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var strategy = new Strategy { ReadyCheck = static _ => true };
        using var pool = inner.WithPreparation(strategy);

        var conn = await pool.AcquireAsync();
        Assert.True(conn.Connected is false or true); // any state, just delivered
        Assert.Equal(1, strategy.ReadyCalls);
        Assert.Equal(0, strategy.PrepareCalls);
    }

    [Fact]
    public async Task NotReadyObject_ShouldBePreparedAndDelivered()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (conn, _) =>
            {
                conn.Connected = true;
                return Task.CompletedTask;
            },
        };
        using var pool = inner.WithPreparation(strategy);

        var first = inner.Acquire();
        first.Connected = false;
        inner.Release(first);

        var conn = await pool.AcquireAsync();
        Assert.Same(first, conn);
        Assert.True(conn.Connected);
        Assert.Equal(1, strategy.PrepareCalls);
    }

    [Fact]
    public async Task FailedPrepare_ShouldDiscardAndRetryWithAnotherObject()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var discarded = 0;
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = (conn, _) =>
            {
                // The parked object is beyond repair; anything fresh prepares fine.
                if (conn.BeyondRepair)
                {
                    return Task.FromException(new InvalidOperationException("dead line"));
                }

                conn.Connected = true;
                return Task.CompletedTask;
            },
        };
        using var pool = inner.WithPreparation(strategy, onDiscard: conn =>
        {
            conn.Dispose();
            discarded++;
        }, maxPrepareAttempts: 3);

        // Park one broken object; the first borrow picks it up and fails to prepare.
        var broken = inner.Acquire();
        broken.Connected = false;
        broken.BeyondRepair = true;
        inner.Release(broken);

        var conn = await pool.AcquireAsync();

        Assert.NotSame(broken, conn);
        Assert.True(conn.Connected);
        Assert.Equal(1, discarded);
        Assert.True(broken.Disposed);
    }

    [Fact]
    public async Task ExhaustedRetryBudget_ShouldThrowPreparationException()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (_, _) => Task.FromException(new InvalidOperationException("smtp down")),
        };
        using var pool = inner.WithPreparation(strategy, maxPrepareAttempts: 3);

        var ex = await Assert.ThrowsAsync<HayatePoolPreparationException>(() => pool.AcquireAsync());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("3 consecutive", ex.Message);
    }

    [Fact]
    public async Task Cancellation_ShouldPropagateAndDiscard()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var discarded = 0;
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (_, ct) => Task.FromCanceled(ct),
        };
        using var pool = inner.WithPreparation(strategy, onDiscard: _ => discarded++);

        // A pre-cancelled token would throw before anything is borrowed; cancel during the
        // prepare instead, so a taken object is discarded on the way out.
        var cancelled = new CancellationToken(canceled: true);
        var strategy2 = new ReconnectingStrategy
        {
            OnPrepare = (_, _) => Task.FromCanceled(cancelled),
        };
        using var pool2 = inner.WithPreparation(strategy2, onDiscard: _ => discarded++);

        // First borrow: not ready -> prepare observes cancellation -> discard + rethrow.
        var parked = inner.Acquire();
        parked.Connected = false;
        inner.Release(parked);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool2.AcquireAsync());
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void SynchronousAcquire_ShouldRunTheSameChain()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (conn, _) =>
            {
                conn.Connected = true;
                return Task.CompletedTask;
            },
        };
        using var pool = inner.WithPreparation(strategy);

        var item = inner.Acquire();
        item.Connected = false;
        inner.Release(item);

        var conn = pool.Acquire(); // blocking chain, same semantics as the async path
        Assert.Same(item, conn);
        Assert.True(conn.Connected);
    }

    [Fact]
    public void DiscardCallbackFailure_ShouldNotMaskThePreparationFailure()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (_, _) => Task.FromException(new InvalidOperationException("boom")),
        };
        using var pool = inner.WithPreparation(
            strategy,
            onDiscard: _ => throw new InvalidCastException("discard blew up"),
            maxPrepareAttempts: 1);

        var ex = Assert.Throws<HayatePoolPreparationException>(() => pool.Acquire());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void OtherMembers_ShouldDelegateToTheInnerPool()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        using var pool = inner.WithPreparation(new Strategy());

        var item = pool.Acquire();
        pool.Release(item);
        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(inner.GetStats().PooledCount, pool.GetStats().PooledCount);
        Assert.Equal(1, pool.Evict(HayateEvictReason.Idle));

        pool.Clear();
        Assert.Equal(0, pool.GetStats().PooledCount);

        pool.SetUnavailable("flag");
        Assert.False(pool.CheckAvailable());
        pool.SetAvailable();
        Assert.True(pool.CheckAvailable());
    }

    [Fact]
    public void InvalidConstruction_ShouldThrow()
    {
        var inner = new HayateUnboundedPool<Connection>(maxIdle: 4);
        Assert.Throws<ArgumentNullException>(() => inner.WithPreparation(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            inner.WithPreparation(new Strategy(), maxPrepareAttempts: 0));
    }

    [Fact]
    public async Task WithTheBoundedEngine_ShouldCompose()
    {
        var inner = new HayatePoolBuilder<Connection>()
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            .WithValidateOnBorrow(false)
            .WithValidateOnReturn(false)
            .Build();

        var strategy = new ReconnectingStrategy
        {
            OnPrepare = static (conn, _) =>
            {
                conn.Connected = true;
                return Task.CompletedTask;
            },
        };
        using var pool = inner.WithPreparation(strategy, onDiscard: static conn => conn.Dispose());

        var conn = await pool.AcquireAsync();
        Assert.True(conn.Connected);

        // Simulate a dropped connection while parked, then borrow again: repaired transparently.
        conn.Connected = false;
        pool.Release(conn);

        var next = await pool.AcquireAsync();
        Assert.Same(conn, next);
        Assert.True(next.Connected);
    }

    private sealed class PooledItem
    {
        public int Value { get; set; }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Specialized cancellation-source pool (G-8): a reusable <see cref="CancellationTokenSource"/> whose
    /// disposal returns it to the pool. Acceptance: a returned source is reset and handed out again where the
    /// runtime can reset one, and destroyed where it cannot (the pool says which through
    /// <c>IsReuseSupported</c>) — either way a rental is never a source whose cancellation has already been
    /// requested, and the pool retains nothing it cannot clean; a canceled source, and a source whose timer
    /// has fired, are destroyed on return rather than parked; <c>RentLinked</c> makes the rental follow an
    /// upstream token without creating a source outside the pool, and a link from a finished rental can never
    /// reach the source's next renter; the callbacks a renter registered do not survive the return; disposal
    /// routes exactly one return, so a double dispose never parks the same source twice; and the pool keeps
    /// serving under concurrent rentals.
    /// </summary>
    public class SpecializedCancellationTokenSourcePoolTests
    {
        [Fact]
        public void Defaults_ShouldMatchTheSpecializedPoolConvention()
        {
            Assert.Equal(16, CancellationTokenSourcePool.DefaultPoolMaximumSize);
            Assert.NotNull(CancellationTokenSourcePool.Instance);

            // The pool's reuse claim is a statement about the runtime, so it is pinned to the runtime rather
            // than left to whatever the behavioural tests happen to observe: if the lookup silently missed
            // TryReset on a framework that has it, the pool would degrade without failing anything else.
    #if NET6_0_OR_GREATER
            Assert.True(CancellationTokenSourcePool.IsReuseSupported,
                "TryReset exists on .NET 6.0 and later, so a returned source must be reusable.");
    #else
            Assert.False(CancellationTokenSourcePool.IsReuseSupported,
                "TryReset does not exist on .NET Framework 4.8, so the pool must not claim reuse.");
    #endif
        }

        [Fact]
        public void InvalidPoolSize_ShouldThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CancellationTokenSourcePool(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CancellationTokenSourcePool(-1));
        }

        [Fact]
        public void Rent_ShouldReturnAFreshUncanceledSource()
        {
            using var pool = new CancellationTokenSourcePool(4);

            using var source = pool.Rent();

            Assert.NotNull(source);
            Assert.False(source.IsCancellationRequested);
            Assert.True(source.Token.CanBeCanceled);
        }

        [Fact]
        public void ReturnedSource_ShouldBeReusedOnlyWhereTheRuntimeCanResetOne()
        {
            using var pool = new CancellationTokenSourcePool(4);

            CancellationTokenSource first;
            using (first = pool.Rent())
            {
            }

            var pooledAfterReturn = pool.GetStats().PooledCount;
            using var second = pool.Rent();

            if (CancellationTokenSourcePool.IsReuseSupported)
            {
                // The reset succeeded, so the source was parked and is handed back out.
                Assert.Equal(1, pooledAfterReturn);
                Assert.Same(first, second);
            }
            else
            {
                // No TryReset on this runtime: the returned source was destroyed instead of parked, because a
                // source the pool cannot clean must not be handed to another renter.
                Assert.Equal(0, pooledAfterReturn);
                Assert.NotSame(first, second);
            }

            Assert.False(second.IsCancellationRequested);
        }

        [Fact]
        public void CanceledSource_ShouldNeverBeHandedOutAgain()
        {
            using var pool = new CancellationTokenSourcePool(4);

            var canceled = pool.Rent();
            canceled.Cancel();
            Assert.True(canceled.IsCancellationRequested);

            canceled.Dispose();

            // The canceled source was destroyed on return: it never enters the idle set again.
            Assert.Equal(0, pool.GetStats().PooledCount);

            using var replacement = pool.Rent();
            Assert.NotSame(canceled, replacement);
            Assert.False(replacement.IsCancellationRequested);

            // ...and the destroyed source is really gone, not merely unpooled.
            Assert.Throws<ObjectDisposedException>(() => canceled.Cancel());
        }

        [Fact]
        public void SourceWhoseTimerHasFired_ShouldNeverBeHandedOutAgain()
        {
            using var pool = new CancellationTokenSourcePool(4);

            var fired = pool.Rent();
            fired.CancelAfter(TimeSpan.Zero);
            Assert.True(WaitForCancellation(fired.Token), "The timer should have canceled the source.");

            fired.Dispose();

            using var replacement = pool.Rent();
            Assert.NotSame(fired, replacement);
            Assert.False(replacement.IsCancellationRequested);
        }

        [Fact]
        public void RentLinked_ShouldFollowTheUpstreamToken()
        {
            using var pool = new CancellationTokenSourcePool(4);
            using var upstream = new CancellationTokenSource();

            using var linked = pool.RentLinked(upstream.Token);
            Assert.False(linked.IsCancellationRequested);

            upstream.Cancel();

            Assert.True(linked.IsCancellationRequested);
        }

        [Fact]
        public void RentLinked_WithAnAlreadyCanceledUpstream_ShouldProduceACanceledRentalThatIsNotReused()
        {
            using var pool = new CancellationTokenSourcePool(4);
            using var upstream = new CancellationTokenSource();
            upstream.Cancel();

            var alreadyCanceled = pool.RentLinked(upstream.Token);
            Assert.True(alreadyCanceled.IsCancellationRequested);

            alreadyCanceled.Dispose();

            using var replacement = pool.Rent();
            Assert.NotSame(alreadyCanceled, replacement);
            Assert.False(replacement.IsCancellationRequested);
        }

        [Fact]
        public void RentLinked_WithANonCancelableToken_ShouldTakeTheOrdinaryRentalPath()
        {
            using var pool = new CancellationTokenSourcePool(4);

            CancellationTokenSource plain;
            using (plain = pool.Rent())
            {
            }

            using var linked = pool.RentLinked(CancellationToken.None);

            if (CancellationTokenSourcePool.IsReuseSupported)
            {
                Assert.Same(plain, linked);
            }
            else
            {
                Assert.NotSame(plain, linked);
            }

            Assert.False(linked.IsCancellationRequested);
        }

        [Fact]
        public void AFormerUpstream_ShouldNotReachAReusedSource()
        {
            using var pool = new CancellationTokenSourcePool(4);
            using var upstream = new CancellationTokenSource();

            var linked = pool.RentLinked(upstream.Token);
            linked.Dispose();

            // Whatever the runtime did with the returned source, the next rental must not be reachable by the
            // finished rental's upstream token.
            using var reused = pool.Rent();
            upstream.Cancel();

            Assert.False(reused.IsCancellationRequested);
        }

        [Fact]
        public void CallbacksRegisteredByAFormerRental_ShouldNotSurviveTheReturn()
        {
            using var pool = new CancellationTokenSourcePool(4);

            var callbacks = 0;
            var first = pool.Rent();

            // Deliberately not disposing the registration: the point is that the renter left a callback behind.
            _ = first.Token.Register(() => Interlocked.Increment(ref callbacks));

            first.Dispose();

            using var second = pool.Rent();
            second.Cancel();

            // The former renter's callback must not fire when the next renter cancels — that is what the reset
            // buys, and on a runtime without one the source was replaced instead of reused.
            Assert.Equal(0, callbacks);
        }

        [Fact]
        public void DoubleDispose_ShouldParkTheSourceOnlyOnce()
        {
            using var pool = new CancellationTokenSourcePool(2);

            var source = pool.Rent();
            source.Dispose();
            source.Dispose();

            // A second return would park the same source twice, so both rentals below would be the same
            // instance.
            using var first = pool.Rent();
            using var second = pool.Rent();

            Assert.NotSame(first, second);
        }

        [Fact]
        public void Clear_ShouldDropRetainedSourcesAndLeaveThePoolUsable()
        {
            using var pool = new CancellationTokenSourcePool(4);

            var retained = pool.Rent();
            retained.Dispose();
            Assert.Equal(CancellationTokenSourcePool.IsReuseSupported ? 1 : 0, pool.GetStats().PooledCount);

            pool.Clear();

            Assert.Equal(0, pool.GetStats().PooledCount);

            // The dropped source is out of the pool: the next rental is a different one.
            using var replacement = pool.Rent();
            Assert.NotSame(retained, replacement);
            Assert.False(replacement.IsCancellationRequested);
        }

        [Fact]
        public void Dispose_ShouldDropRetainedSourcesAndDestroyARentalThatComesBack()
        {
            var pool = new CancellationTokenSourcePool(4);

            var retained = pool.Rent();
            retained.Dispose();

            var outstanding = pool.Rent();
            pool.Dispose();

            // The pool keeps nothing after disposal...
            Assert.Equal(0, pool.GetStats().PooledCount);

            // ...and a rental that comes back afterwards is destroyed rather than parked, because the pool no
            // longer tracks it.
            outstanding.Dispose();
            Assert.Throws<ObjectDisposedException>(() => outstanding.Cancel());
        }

        [Fact]
        public void RentalDisposedAfterThePoolIsClosed_ShouldBeDestroyedForReal()
        {
            var pool = new CancellationTokenSourcePool(4);

            var outstanding = pool.Rent();
            pool.Dispose();

            // The pool no longer tracks the rental, so the return destroys it instead of parking it.
            outstanding.Dispose();

            Assert.Throws<ObjectDisposedException>(() => outstanding.Cancel());
        }

        [Fact]
        public void ConcurrentRentals_ShouldNeverHandOutACanceledSource()
        {
            using var pool = new CancellationTokenSourcePool(8);
            var canceledHandOuts = 0;

            Parallel.For(0, 16, _ =>
            {
                for (var iteration = 0; iteration < 2_000; iteration++)
                {
                    var source = pool.Rent();
                    if (source.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref canceledHandOuts);
                    }

                    if ((iteration & 7) == 0)
                    {
                        source.Cancel();
                    }

                    source.Dispose();
                }
            });

            Assert.Equal(0, canceledHandOuts);
            Assert.True(pool.GetStats().PooledCount <= 8);
        }

        private static bool WaitForCancellation(CancellationToken token)
            => token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
    }
}

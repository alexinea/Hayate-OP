using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Pool-level availability circuit breaker (EnableCircuitBreaker, default off).
    /// Acceptance: consecutive dependency failure reports trip the breaker and the whole pool fails fast;
    /// a successful background probe recovers the pool and fires OnAvailable exactly once; without a probe
    /// the pool stays unavailable until SetAvailable; the failure streak survives borrows and is cleared by
    /// recovery, so sporadic failures separated by a recovery count from scratch; the lean profile forces the
    /// feature off; the options deep-copy contract keeps mutating a copy from reconfiguring the source.
    /// </summary>
    public class CircuitBreakerTests
    {
        private class TestObject { }

        private static HayatePoolBuilder<TestObject> BaseBuilder()
        {
            return new HayatePoolBuilder<TestObject>()
                .WithPoolName("cb-pool")
                .WithMinSize(2)
                .WithMaxSize(4);
        }

        [Fact]
        public void DisabledBreaker_ShouldIgnoreFailureReports_AndAlwaysBeAvailable()
        {
            using (var pool = BaseBuilder().Build())   // EnableCircuitBreaker defaults to false
            {
                for (var i = 0; i < 5; i++) pool.SetUnavailable("dependency down");

                Assert.True(pool.CheckAvailable());

                var item = pool.Acquire();
                Assert.NotNull(item);
                pool.Release(item);
            }
        }

        [Fact]
        public void ThresholdConsecutiveFailures_ShouldTrip_AndFailFastTheWholePool()
        {
            var unavailableCount = new int[1];
            using (var pool = BaseBuilder()
                .WithCircuitBreaker(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5))
                .WithOnUnavailable(_ => Interlocked.Increment(ref unavailableCount[0]))
                .Build())
            {
                // The pool serves normally before the trip.
                var pre = pool.Acquire();
                pool.Release(pre);
                var acquiredBeforeTrip = pool.GetStats().TotalAcquired;

                Assert.True(pool.CheckAvailable());

                // Two reports below the threshold change nothing.
                pool.SetUnavailable("connection failed 1");
                pool.SetUnavailable("connection failed 2");
                Assert.True(pool.CheckAvailable());

                // The third consecutive report trips the breaker exactly once.
                pool.SetUnavailable("connection failed 3");
                Assert.Equal(1, Volatile.Read(ref unavailableCount[0]));
                Assert.False(pool.CheckAvailable());

                // Fail-fast: the borrow throws before doing any pool work — even though idle objects exist,
                // nothing is handed out and the borrow counter does not advance.
                HayatePoolUnavailableException exception = null!;
                try { pool.Acquire(); }
                catch (HayatePoolUnavailableException ex) { exception = ex; }

                Assert.NotNull(exception);
                Assert.Equal("cb-pool", exception.PoolName);
                Assert.Equal("connection failed 3", exception.Reason);
                Assert.Equal(acquiredBeforeTrip, pool.GetStats().TotalAcquired);

                // Further reports while open are no-ops: no second announcement.
                pool.SetUnavailable("connection failed 4");
                Assert.Equal(1, Volatile.Read(ref unavailableCount[0]));
                Assert.False(pool.CheckAvailable());
            }
        }

        [Fact]
        public void SuccessfulProbe_ShouldRecover_AndFireOnAvailableOnce()
        {
            var unavailableCount = new int[1];
            var availableCount = new int[1];
            using (var pool = BaseBuilder()
                .WithCircuitBreaker(1, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100), () => true)
                .WithOnUnavailable(_ => Interlocked.Increment(ref unavailableCount[0]))
                .WithOnAvailable(_ => Interlocked.Increment(ref availableCount[0]))
                .Build())
            {
                pool.SetUnavailable("dependency down");
                Assert.False(pool.CheckAvailable());
                Assert.Equal(1, Volatile.Read(ref unavailableCount[0]));

                // First probe runs one reset-timeout after the trip, then one per probe interval; poll with a
                // generous deadline so a slow machine cannot flake the case.
                var recovered = false;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    if (pool.CheckAvailable()) { recovered = true; break; }
                    Thread.Sleep(25);
                }

                Assert.True(recovered);
                Assert.Equal(1, Volatile.Read(ref availableCount[0]));

                // The pool serves again and the callback carried no failure reason.
                var item = pool.Acquire();
                Assert.NotNull(item);
                pool.Release(item);
            }
        }

        [Fact]
        public void FailingProbe_ShouldKeepThePoolUnavailable()
        {
            var availableCount = new int[1];
            var probeCalls = new int[1];
            using (var pool = BaseBuilder()
                .WithCircuitBreaker(1, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80),
                    () => { Interlocked.Increment(ref probeCalls[0]); return false; })
                .WithOnAvailable(_ => Interlocked.Increment(ref availableCount[0]))
                .Build())
            {
                pool.SetUnavailable("dependency down");

                // Give the probe several intervals to run; it must never recover the pool.
                Thread.Sleep(600);

                Assert.False(pool.CheckAvailable());
                Assert.True(Volatile.Read(ref probeCalls[0]) >= 1);
                Assert.Equal(0, Volatile.Read(ref availableCount[0]));
            }
        }

        [Fact]
        public void NoProbe_ShouldStayUnavailable_UntilSetAvailable()
        {
            var availableCount = new int[1];
            using (var pool = BaseBuilder()
                .WithCircuitBreaker(1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5))
                .WithOnAvailable(_ => Interlocked.Increment(ref availableCount[0]))
                .Build())
            {
                pool.SetUnavailable("dependency down");
                Assert.False(pool.CheckAvailable());

                // The reset timeout elapses but no probe is configured, so nothing recovers the pool.
                Thread.Sleep(250);
                Assert.False(pool.CheckAvailable());

                // Manual recovery closes the breaker exactly once; SetAvailable on an available pool raises nothing.
                pool.SetAvailable();
                Assert.True(pool.CheckAvailable());
                Assert.Equal(1, Volatile.Read(ref availableCount[0]));

                pool.SetAvailable();
                Assert.Equal(1, Volatile.Read(ref availableCount[0]));
            }
        }

        [Fact]
        public void Streak_ShouldBeClearedByRecovery_AndBySetAvailableOnAnAvailablePool()
        {
            using (var pool = BaseBuilder()
                .WithCircuitBreaker(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5))
                .Build())
            {
                // Two failures, then an explicit recovery report: the streak resets, so two more failures still
                // do not trip — the breaker needs three *consecutive* reports.
                pool.SetUnavailable("transient 1");
                pool.SetUnavailable("transient 2");
                pool.SetAvailable();
                pool.SetUnavailable("transient 3");
                pool.SetUnavailable("transient 4");
                Assert.True(pool.CheckAvailable());

                // SetAvailable on an available pool also clears a pending streak.
                pool.SetAvailable();
                pool.SetUnavailable("transient 5");
                pool.SetUnavailable("transient 6");
                Assert.True(pool.CheckAvailable());

                // The third consecutive report (without an intervening recovery) finally trips.
                pool.SetUnavailable("transient 7");
                Assert.False(pool.CheckAvailable());
            }
        }

        [Fact]
        public void LeanProfile_ShouldForceTheBreakerOff()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithMinSize(0)
                .WithLeanProfile()
                .WithEnableCircuitBreaker()          // even after the profile: lean is a mode, not a knob
                .Build())
            {
                Assert.False(pool.GetOptions().EnableCircuitBreaker);

                pool.SetUnavailable("ignored in lean mode");
                Assert.True(pool.CheckAvailable());

                var item = pool.Acquire();
                Assert.NotNull(item);
                pool.Release(item);
            }
        }

        [Fact]
        public void OptionsCopyTo_ShouldDeepCopyTheBreakerSettings()
        {
            Func<bool> probe = () => true;
            var original = new HayatePoolOptions
            {
                EnableCircuitBreaker = true,
                OnAvailable = _ => { },
                OnUnavailable = _ => { }
            };
            original.CircuitBreaker.FailureThreshold = 7;
            original.CircuitBreaker.ResetTimeout = TimeSpan.FromSeconds(11);
            original.CircuitBreaker.ProbeInterval = TimeSpan.FromSeconds(13);
            original.CircuitBreaker.Probe = probe;

            var copy = original.CopyTo();

            // The settings object is copied, not shared, so mutating the copy cannot reconfigure the pool.
            Assert.False(ReferenceEquals(original.CircuitBreaker, copy.CircuitBreaker));
            Assert.Equal(original.CircuitBreaker, copy.CircuitBreaker);
            Assert.Same(probe, copy.CircuitBreaker.Probe);
            Assert.Equal(original.OnAvailable, copy.OnAvailable);

            copy.CircuitBreaker.FailureThreshold = 99;
            copy.EnableCircuitBreaker = false;
            Assert.Equal(7, original.CircuitBreaker.FailureThreshold);
            Assert.True(original.EnableCircuitBreaker);

            // Normalization clamps out-of-range breaker settings to their defaults.
            var raw = new HayatePoolOptions { EnableCircuitBreaker = true };
            raw.CircuitBreaker.FailureThreshold = 0;
            raw.CircuitBreaker.ResetTimeout = TimeSpan.Zero;
            raw.CircuitBreaker.ProbeInterval = TimeSpan.FromSeconds(-1);
            raw.ApplyFeatureSwitches();
            Assert.Equal(3, raw.CircuitBreaker.FailureThreshold);
            Assert.Equal(TimeSpan.FromSeconds(30), raw.CircuitBreaker.ResetTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), raw.CircuitBreaker.ProbeInterval);
        }
    }
}

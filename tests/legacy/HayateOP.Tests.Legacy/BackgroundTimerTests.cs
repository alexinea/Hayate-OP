using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Merged background scheduler -- eviction, auto-scaling and idle validation are driven by a single
    /// timer instead of one timer per concern.
    /// Acceptance: a pool whose three background features are all disabled holds no timer at all (so an
    /// idle pool performs no periodic wake-ups); enabling any of them materializes exactly one timer no
    /// matter how many are on; each concern still fires on its own configured period through the shared
    /// dispatch; and the timer handle is released on Dispose.
    /// </summary>
    public class BackgroundTimerTests
    {
        private class TestObject { }

        /// <summary>
        /// Policy whose validation verdict can be flipped after construction, so the idle-validation pass can
        /// be observed destroying objects it considered valid at pre-warm time.
        /// </summary>
        private class SwitchableValidatePolicy : IHayateObjectPolicy<TestObject>
        {
            public volatile bool Valid = true;

            public TestObject Create() => new TestObject();

            public bool OnRelease(TestObject item) => true;

            public bool Validate(TestObject item) => Valid;

            public void OnAcquire(TestObject item) { }

            public void OnPassivate(TestObject item) { }

            public void OnDestroy(TestObject item) { }
        }

        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// Returns every live (non-null) <see cref="Timer"/> held in the pool's private instance fields.
        /// The concrete pool type is only reachable through reflection here, and the point of the assertion
        /// is precisely that this set is empty -- so the helper enumerates all Timer-typed fields rather than
        /// a hard-coded name.
        /// </summary>
        private static Timer[] LiveTimers(IHayateObjectPool<TestObject> pool)
        {
            return pool.GetType()
                .GetFields(PrivateInstance)
                .Where(f => typeof(Timer).IsAssignableFrom(f.FieldType))
                .Select(f => f.GetValue(pool) as Timer)
                .Where(t => t != null)
                .Select(t => t!)
                .ToArray();
        }

        private static long PeriodTicks(IHayateObjectPool<TestObject> pool, string fieldName)
        {
            var field = pool.GetType().GetField(fieldName, PrivateInstance);
            Assert.NotNull(field);
            return (long)field!.GetValue(pool)!;
        }

        [Fact]
        public void AllBackgroundFeaturesDisabled_ShouldNotCreateAnyTimer()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-all-off")
                .WithEnableEviction(false)
                .WithEnableAutoScaling(false)
                .WithEnableValidation(false)
                .Build())
            {
                // The pool is fully functional, it just has no background work to schedule.
                var obj = pool.Acquire();
                pool.Release(obj);
                Assert.NotNull(obj);

                Assert.Empty(LiveTimers(pool));
                Assert.Equal(0, PeriodTicks(pool, "_evictionPeriodTicks"));
                Assert.Equal(0, PeriodTicks(pool, "_scalingPeriodTicks"));
                Assert.Equal(0, PeriodTicks(pool, "_validationPeriodTicks"));
            }
        }

        [Fact]
        public void SingleBackgroundFeatureEnabled_ShouldCreateExactlyOneTimer()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-eviction-only")
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)
                .WithEnableAutoScaling(false)
                .WithEnableValidation(false)
                .Build())
            {
                Assert.Single(LiveTimers(pool));
                Assert.True(PeriodTicks(pool, "_evictionPeriodTicks") > 0);

                // The two concerns that are off must not be scheduled on the shared tick.
                Assert.Equal(0, PeriodTicks(pool, "_scalingPeriodTicks"));
                Assert.Equal(0, PeriodTicks(pool, "_validationPeriodTicks"));
            }
        }

        [Fact]
        public void AllThreeFeaturesEnabled_ShouldStillCreateExactlyOneTimer()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-all-on")
                .WithEnableEviction(true)
                .WithEvictionInterval(2000)
                .WithEnableAutoScaling(true)
                .WithScalingInterval(200)
                // Keep both cooldowns far beyond the test's lifetime: this fact asserts the schedule, not scaling behavior.
                .WithScaleUpCooldownSeconds(3600)
                .WithScaleDownCooldownSeconds(3600)
                .WithEnableValidation(true)
                .WithValidateWhileIdle(true)
                .WithValidateInterval(3000)
                .Build())
            {
                // Three concerns, one timer: the wake-up count is bounded by the smallest period, not by their sum.
                Assert.Single(LiveTimers(pool));

                var evictionPeriod = PeriodTicks(pool, "_evictionPeriodTicks");
                var scalingPeriod = PeriodTicks(pool, "_scalingPeriodTicks");
                var validationPeriod = PeriodTicks(pool, "_validationPeriodTicks");

                Assert.True(evictionPeriod > 0);
                Assert.True(scalingPeriod > 0);
                Assert.True(validationPeriod > 0);

                // Each concern keeps its own cadence: the shortest configured interval yields the shortest period.
                Assert.True(scalingPeriod < evictionPeriod);
                Assert.True(scalingPeriod < validationPeriod);
            }
        }

        [Fact]
        public void BackgroundEviction_ShouldStillRunThroughSharedTimer()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-eviction-runs")
                .WithMinSize(3)
                .WithMaxSize(6)
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)   // builder lower bound
                .WithMaxIdleTime(TimeSpan.FromMilliseconds(100))
                .WithEnableAutoScaling(false)
                .WithEnableValidation(false)
                .Build())
            {
                Assert.Equal(3, pool.GetStats().PooledCount);

                // The idle objects exceed MaxIdleTime well before the first tick; the shared timer must still drive them out.
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (pool.GetStats().PooledCount > 0 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }

                Assert.Equal(0, pool.GetStats().PooledCount);
            }
        }

        [Fact]
        public void BackgroundIdleValidation_ShouldStillRunThroughSharedTimer()
        {
            var policy = new SwitchableValidatePolicy();

            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-validation-runs")
                .WithPolicy(policy)
                .WithMinSize(3)
                .WithMaxSize(6)
                .WithEnableEviction(false)
                .WithEnableAutoScaling(false)
                .WithEnableValidation(true)
                .WithValidateWhileIdle(true)
                .WithValidateInterval(1000)   // builder lower bound
                .Build())
            {
                Assert.Equal(3, pool.GetStats().PooledCount);

                // Every pre-warmed object now fails validation; only the background idle pass can remove them
                // (borrow and return validation are both off, and no borrow happens here).
                policy.Valid = false;

                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (pool.GetStats().PooledCount > 0 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }

                Assert.Equal(0, pool.GetStats().PooledCount);
            }
        }

        [Fact]
        public void Dispose_ShouldReleaseTheSharedTimer()
        {
            var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("o3-dispose")
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)
                .WithEnableAutoScaling(false)
                .WithEnableValidation(false)
                .Build();

            Assert.Single(LiveTimers(pool));

            pool.Dispose();

            // Disposed pools must not retain the timer handle.
            Assert.Empty(LiveTimers(pool));
        }
    }
}

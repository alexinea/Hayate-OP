using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Eviction sampling changed from "calling GetAll().ToArray() every time" to reusing the buffered SnapshotHead(buffer, limit),
    /// avoiding a full array distribution over the entire idle linked list on every call (long-tail allocation). This case verifies that under a bounded sampling window,
    /// eviction still converges correctly across cycles and clears expired objects, covering SnapshotHead's bounded-copy and buffer-reuse paths.
    /// </summary>
    public class EvictionSamplingTests
    {
        private class TestObject { }

        [Fact(Timeout = 60000)]
        public void Eviction_WithBoundedSampleWindow_EventuallyEvictsExpired()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)
                .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
                .WithMinSize(12)
                .WithNumTestsPerEvictionRun(3)
                .Build();

            Assert.Equal(12, pool.GetStats().PooledCount);

            // The sampling window is 3, far smaller than the number of idle objects, so eviction must span multiple cycles to cover them all;
            // after waiting long enough, all expired idle objects should be cleared, verifying the buffer-reuse + bounded-copy logic.
            Thread.Sleep(6000);

            Assert.Equal(0, pool.GetStats().PooledCount);
        }
    }
}
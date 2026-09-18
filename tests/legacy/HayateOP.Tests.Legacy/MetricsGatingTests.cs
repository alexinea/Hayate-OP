using System;
using System.Threading;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using Moq;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Counter gating contract. `EnableMetrics` gates three of the four cumulative traffic counters and the
    /// timing statistics, but <c>TotalAcquired</c> is deliberately written unconditionally because it is the
    /// borrow-count contract that callers and tests verify with metrics off. The leak-detection and
    /// allocation-tracking counters follow their own switches rather than the metrics switch.
    /// `EnableDiagnostics` (O11) sits above all of them as the master switch of the diagnostic surface: with
    /// it off no counter (including <c>TotalAcquired</c>), no metrics callback and no per-operation trace
    /// runs, which is what makes the hot path allocation-free.
    /// Acceptance: this split is asserted rather than described, so it cannot drift silently.
    /// </summary>
    public class MetricsGatingTests
    {
        private class TestObject { }

        /// <summary>
        /// Counts the debug-level entries the engine emits. Only the debug channel is counted: the master
        /// switch is documented to silence the per-operation trace without touching lifecycle and problem
        /// logs, and this logger is what holds that to a number.
        /// </summary>
        private sealed class CountingLogger : IHayateLogger
        {
            public int DebugCount;

            public void LogInformation(string message, params object[] args) { }
            public void LogWarning(string message, params object[] args) { }
            public void LogError(Exception ex, string message, params object[] args) { }
            public void LogDebug(string message, params object[] args) => Interlocked.Increment(ref DebugCount);
        }

        [Fact]
        public void MetricsOff_ShouldStillCountBorrowsButNotTheGatedCounters()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-off")
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithEnableMetrics(false)
                .Build())
            {
                var a = pool.Acquire();
                var b = pool.Acquire();
                var c = pool.Acquire();
                pool.Release(a);
                pool.Release(b);
                pool.Release(c);

                var stats = pool.GetStats();

                // The borrow-count contract is metrics-independent.
                Assert.Equal(3, stats.TotalAcquired);

                // The other three cumulative counters are gated by metrics, so they never move.
                Assert.Equal(0, stats.TotalCreated);
                Assert.Equal(0, stats.TotalReleased);
                Assert.Equal(0, stats.TotalMissed);
                Assert.Equal(0, stats.WaitTimeCount);
                Assert.Equal(0, stats.LeaseTimeCount);
            }
        }

        [Fact]
        public void MetricsOn_ShouldAdvanceTheGatedCounters()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-on")
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithEnableMetrics(true)
                .Build())
            {
                var a = pool.Acquire();
                var b = pool.Acquire();
                var c = pool.Acquire();
                pool.Release(a);
                pool.Release(b);
                pool.Release(c);

                var stats = pool.GetStats();

                Assert.Equal(3, stats.TotalAcquired);
                Assert.Equal(4, stats.TotalCreated);   // the four pre-warmed objects
                Assert.Equal(3, stats.TotalReleased);
                Assert.Equal(0, stats.TotalMissed);
                Assert.Equal(3, stats.WaitTimeCount);
                Assert.Equal(3, stats.LeaseTimeCount);
            }
        }

        [Fact]
        public void TotalMissed_ShouldFollowTheMetricsGate()
        {
            // An empty pool under the Abort policy rejects immediately, which is the cheapest deterministic
            // way to produce a miss. Cold boot does not run on this branch, so nothing is ever created.
            using (var metricsOff = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-miss-off")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
                .WithEnableMetrics(false)
                .Build())
            {
                Assert.Throws<InvalidOperationException>(() => { metricsOff.Acquire(); });
                Assert.Equal(0, metricsOff.GetStats().TotalMissed);
            }

            using (var metricsOn = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-miss-on")
                .WithMinSize(0)
                .WithMaxSize(4)
                .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
                .WithEnableMetrics(true)
                .Build())
            {
                Assert.Throws<InvalidOperationException>(() => { metricsOn.Acquire(); });
                Assert.Equal(1, metricsOn.GetStats().TotalMissed);
            }
        }

        [Fact]
        public void LeakCounters_ShouldFollowTheLeakDetectionGate_NotMetrics()
        {
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-leak")
                .WithMinSize(1)
                .WithMaxSize(4)
                // Leak detection off, so the snapshot reports a *suspected* leak instead of a detected one --
                // and metrics off, which must not suppress either counter.
                .WithEnableLeakDetection(false)
                .WithEnableMetrics(false)
                .WithEnableEviction(false)
                .WithEnableValidation(false)
                .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(50))
                .Build())
            {
                var held = pool.Acquire();
                Assert.NotNull(held);
                // Deliberately not released: the object stays borrowed past the threshold. Comfortably above
                // the 50 ms threshold so machine load cannot make the comparison marginal.
                Thread.Sleep(250);

                var snapshot = pool.TakeSnapshot();

                Assert.True(snapshot.LeakSuspectedCount >= 1);
                Assert.Equal(0, snapshot.LeakCount);            // leak detection is off, so nothing is "detected"
                Assert.Equal(0, pool.GetStats().TotalReleased); // metrics are off, so the return counter never moved
            }
        }

        [Fact]
        public void AllocationCounters_ShouldFollowTheAllocationTrackingGate_NotMetrics()
        {
            using (var trackingOff = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-alloc-off")
                .WithMaxSize(8)
                .WithEnableMetrics(false)
                .WithEnableAllocationTracking(false)
                .Build())
            {
                var a = trackingOff.Acquire();
                trackingOff.Release(a);

                var offStats = trackingOff.GetStats();
                Assert.False(offStats.AllocationTrackingEnabled);
                Assert.Equal(0, offStats.AcquireAllocationSamples);
                Assert.Equal(0, offStats.ReleaseAllocationSamples);
                Assert.Equal(0, offStats.AcquireAllocatedBytes);
                Assert.Equal(0, offStats.ReleaseAllocatedBytes);
            }

            using (var trackingOn = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-alloc-on")
                .WithMaxSize(8)
                .WithEnableMetrics(false)
                .WithEnableAllocationTracking(true)
                .Build())
            {
                var b = trackingOn.Acquire();
                trackingOn.Release(b);

                var onStats = trackingOn.GetStats();
                // Samples are counted on every target framework; the byte delta itself is only available on
                // net6.0+ (net48 and netstandard2.0 report 0), so only the sample counts are asserted.
                Assert.True(onStats.AllocationTrackingEnabled);
                Assert.True(onStats.AcquireAllocationSamples >= 1);
                Assert.True(onStats.ReleaseAllocationSamples >= 1);
            }
        }

        [Fact]
        public void DiagnosticsOff_ShouldZeroEveryCounterIncludingTotalAcquired()
        {
            // The master switch is the one configuration in which the borrow-count contract is not kept:
            // the point of the switch is that no bookkeeping survives on the borrow/return path at all.
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-diagnostics-off")
                .WithMinSize(4)
                .WithMaxSize(16)
                // Both sub-switches are asked for and normalized away by the master switch, so this also
                // pins the "master gate wins over its sub-switches" rule.
                .WithEnableMetrics(true)
                .WithEnableAllocationTracking(true)
                .WithEnableDiagnostics(false)
                .Build())
            {
                var a = pool.Acquire();
                var b = pool.Acquire();
                var c = pool.Acquire();
                pool.Release(a);
                pool.Release(b);
                pool.Release(c);

                var stats = pool.GetStats();

                Assert.Equal(0, stats.TotalAcquired);
                Assert.Equal(0, stats.TotalCreated);
                Assert.Equal(0, stats.TotalReleased);
                Assert.Equal(0, stats.TotalMissed);
                Assert.Equal(0, stats.WaitTimeCount);
                Assert.Equal(0, stats.LeaseTimeCount);
                Assert.False(stats.AllocationTrackingEnabled);
                Assert.Equal(0, stats.AcquireAllocationSamples);
                Assert.Equal(0, stats.ReleaseAllocationSamples);

                // The pool still works — only its bookkeeping is gone. Both of these are read from the live
                // shard structures rather than from a counter, which is exactly why they keep moving.
                Assert.Equal(4, stats.PooledCount);
                Assert.Equal(4, stats.CurrentSize);

                // The collapsed configuration is visible on the options object rather than hidden in the engine.
                var options = pool.GetOptions();
                Assert.False(options.EnableDiagnostics);
                Assert.False(options.EnableMetrics);
                Assert.False(options.EnableAllocationTracking);
            }
        }

        [Fact]
        public void DiagnosticsOn_ShouldStillWriteTotalAcquiredWithMetricsOff()
        {
            // The other side of the same coin: the master switch is what gates TotalAcquired, so the
            // metrics switch alone must leave the documented contract untouched.
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-diagnostics-on")
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithEnableMetrics(false)
                .WithEnableDiagnostics(true)
                .Build())
            {
                var a = pool.Acquire();
                var b = pool.Acquire();
                pool.Release(a);
                pool.Release(b);

                var stats = pool.GetStats();
                Assert.Equal(2, stats.TotalAcquired);
                Assert.Equal(0, stats.TotalReleased);
            }
        }

        [Fact]
        public void DiagnosticsOff_ShouldSuppressThePerOperationTrace()
        {
            var quiet = new CountingLogger();
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-trace-off")
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithLogger(quiet)
                .WithEnableDiagnostics(false)
                .Build())
            {
                for (var i = 0; i < 16; i++)
                {
                    var item = pool.Acquire();
                    pool.Release(item);
                }
            }

            Assert.Equal(0, quiet.DebugCount);

            // Control: with diagnostics left on the same loop produces traces (this is what the switch
            // removes, and what the allocation assertion below rests on).
            var chatty = new CountingLogger();
            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("gating-trace-on")
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithLogger(chatty)
                .WithEnableDiagnostics(true)
                .Build())
            {
                for (var i = 0; i < 16; i++)
                {
                    var item = pool.Acquire();
                    pool.Release(item);
                }
            }

            Assert.True(chatty.DebugCount > 0);
        }

        [Fact]
        public void DiagnosticsOff_WithACustomMetricsSink_ShouldFailFast()
        {
            // A sink that can never be called is a configuration mistake, not a silent no-op: the same
            // rule the metrics switch has enforced since 2.2, extended to the master switch that closes
            // metrics with it. The message names the switch that has to be re-opened.
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using (var pool = new HayatePoolBuilder<TestObject>()
                    .WithEnableMetrics(true)
                    .WithMetrics(new Mock<IHayateMetrics>().Object)
                    .WithEnableDiagnostics(false)
                    .Build())
                {
                }
            });

            Assert.Contains("WithMetrics", ex.Message);
            Assert.Contains("WithEnableDiagnostics(true)", ex.Message);
        }

        // net48 lacks GC.GetAllocatedBytesForCurrentThread (the same condition the engine's allocation
        // tracking branches on), so the allocation measurement runs on the net6.0 / net7.0 legs only.
#if !NETFRAMEWORK && !NETSTANDARD2_0
        [Fact]
        public void DiagnosticsOff_ShouldNotAllocateOnTheWarmedBorrowPath()
        {
            // The diagnostic surface is measured on the borrow phase alone, because the return phase carries
            // one allocation the switches do not own: every return appends a fresh LinkedListNode to the
            // shard free list. Holding the returns outside the measured window isolates what the master
            // switch actually removes — the params array (and the boxed arguments behind it) that every
            // per-operation trace built.
            var off = MeasureWarmedBorrowAllocations(diagnostics: false);
            Assert.Equal(0L, off);

            // Control: the identical loop allocates as soon as the traces are back on, so the 0 above is the
            // switch's doing rather than a loop that never reaches the trace.
            var on = MeasureWarmedBorrowAllocations(diagnostics: true);
            Assert.True(on > 0);
        }

        /// <summary>
        /// Borrows and returns a whole pool's worth of objects repeatedly, accumulating only the allocation
        /// performed by the borrow phase of each round. The returns run between the windows, so the shard
        /// free-list node they allocate is excluded.
        /// </summary>
        private static long MeasureWarmedBorrowAllocations(bool diagnostics)
        {
            const int Items = 16;
            const int Rounds = 200;

            using (var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName(diagnostics ? "gating-borrow-alloc-on" : "gating-borrow-alloc-off")
                .WithMinSize(Items)
                .WithMaxSize(Items)
                .WithShardCount(4)
                .WithEnableDiagnostics(diagnostics)
                .Build())
            {
                var held = new TestObject[Items];

                // Warm-up round: establishes the steady state (nothing is created or destroyed from here on)
                // outside the measured windows.
                for (var i = 0; i < Items; i++) held[i] = pool.Acquire();
                for (var i = 0; i < Items; i++) pool.Release(held[i]);

                // Touch the measuring API once so its own first call cannot land inside a window.
                _ = GC.GetAllocatedBytesForCurrentThread();

                var total = 0L;
                for (var round = 0; round < Rounds; round++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < Items; i++) held[i] = pool.Acquire();
                    total += GC.GetAllocatedBytesForCurrentThread() - before;

                    for (var i = 0; i < Items; i++) pool.Release(held[i]);
                }

                return total;
            }
        }
#endif
    }
}

using DotNetCore.HayateOP.Logging;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// 3.0 L7: the members <see cref="IHayateLogger"/> gained, the migration
    /// <see cref="HayateLoggerBase"/> is meant to make cheap, and the <c>IsEnabled</c> permission check the
    /// pool now honours on its borrow and release paths.
    /// </summary>
    public class HayateLoggerContractTests
    {
        private class TestObject { }

        /// <summary>
        /// The migration, written the only way the compiler accepts it: the class clause becomes
        /// <c>: HayateLoggerBase</c> and the four methods 2.x had gain <c>override</c>. Nothing new has to
        /// be implemented — that is the point of the class, and it is what the reflection guard below pins
        /// down.
        /// </summary>
        private sealed class MigratedLogger : HayateLoggerBase
        {
            public List<string> Lines { get; } = new();

            public override void LogInformation(string message, params object[] args) => Lines.Add("INFO " + message);
            public override void LogWarning(string message, params object[] args) => Lines.Add("WARN " + message);
            public override void LogError(Exception ex, string message, params object[] args) => Lines.Add("ERROR " + message);
            public override void LogDebug(string message, params object[] args) => Lines.Add("DEBUG " + message);
        }

        /// <summary>
        /// A logger that only wants one level, so it overrides one method and inherits the rest.
        /// </summary>
        private sealed class MinimalLogger : HayateLoggerBase
        {
            public int InformationCalls;

            public override void LogInformation(string message, params object[] args) => InformationCalls++;
            public override void LogDebug(string message, params object[] args) { }
            public override void LogWarning(string message, params object[] args) { }
            public override void LogError(Exception ex, string message, params object[] args) { }
        }

        /// <summary>
        /// Counts the debug channel and the lifecycle channel. The debug channel is answered as disabled,
        /// but <see cref="LogDebug"/> is still implemented — the gate has to be what keeps the call from
        /// arriving, not the method being absent.
        /// </summary>
        private sealed class DebugSuppressedLogger : HayateLoggerBase
        {
            public int DebugCalls;
            public int InformationCalls;

            public override bool IsEnabled(HayateLogLevel level) => level != HayateLogLevel.Debug;

            public override void LogDebug(string message, params object[] args) => Interlocked.Increment(ref DebugCalls);
            public override void LogInformation(string message, params object[] args) => Interlocked.Increment(ref InformationCalls);
            public override void LogWarning(string message, params object[] args) { }
            public override void LogError(Exception ex, string message, params object[] args) { }
        }

        /// <summary>
        /// The same logger with the gate open: enabled for everything, discards everything. The only
        /// difference from <see cref="DebugSuppressedLogger"/> is what <c>IsEnabled</c> answers, which is
        /// what makes the allocation comparison below a measurement of the gate.
        /// </summary>
        private sealed class OpenDebugLogger : HayateLoggerBase
        {
            public int DebugCalls;

            public override void LogDebug(string message, params object[] args) => Interlocked.Increment(ref DebugCalls);
            public override void LogInformation(string message, params object[] args) { }
            public override void LogWarning(string message, params object[] args) { }
            public override void LogError(Exception ex, string message, params object[] args) { }
        }

        /// <summary>
        /// A logger that implements <see cref="IHayateLogger"/> directly — the 2.x shape, and the shape a
        /// caller is left with if they decline <see cref="HayateLoggerBase"/>. Present only as the base of
        /// <see cref="HidingDerivative"/>; it is not the recommended form, since 3.0 added four members and
        /// every one of them has to be written out here.
        /// </summary>
        private class DirectImplementation : IHayateLogger
        {
            public List<string> Lines { get; } = new();

            public void LogTrace(string message, params object[] args) => Lines.Add("TRACE " + message);
            public void LogDebug(string message, params object[] args) => Lines.Add("DEBUG " + message);
            public void LogInformation(string message, params object[] args) => Lines.Add("INFO " + message);
            public void LogWarning(string message, params object[] args) => Lines.Add("WARN " + message);
            public void LogError(Exception ex, string message, params object[] args) => Lines.Add("ERROR " + message);
            public void LogCritical(Exception ex, string message, params object[] args) => Lines.Add("CRITICAL " + message);
            public bool IsEnabled(HayateLogLevel level) => true;
            public IDisposable BeginScope(string message, params object[] args) => HayateLoggerBase.NullScope;
        }

        /// <summary>
        /// Derives from <see cref="DirectImplementation"/> and hides one method instead of overriding it.
        /// </summary>
        private sealed class HidingDerivative : DirectImplementation
        {
            public new void LogInformation(string message, params object[] args) => Lines.Add("HIDDEN " + message);
        }

        // ------------------------------------------------------------------ the level enum and its mapping

        [Fact]
        public void HayateLogLevel_ShouldCarryTheMicrosoftExtensionsLogLevelValues()
        {
            // The bridge casts rather than maps, so the two enums have to agree numerically. If this fails,
            // HayateMicrosoftLoggerAdapter.IsEnabled is filtering at the wrong level and nobody notices
            // until an entry goes missing.
            Assert.Equal(0, (int)HayateLogLevel.Trace);
            Assert.Equal(1, (int)HayateLogLevel.Debug);
            Assert.Equal(2, (int)HayateLogLevel.Information);
            Assert.Equal(3, (int)HayateLogLevel.Warning);
            Assert.Equal(4, (int)HayateLogLevel.Error);
            Assert.Equal(5, (int)HayateLogLevel.Critical);
            Assert.Equal(6, (int)HayateLogLevel.None);
        }

        // ------------------------------------------------------------------ the base class defaults

        [Fact]
        public void BaseClass_ShouldAnswerEnabledForEveryRealLevel_AndDisabledForNone()
        {
            var logger = new MinimalLogger();

            foreach (var level in new[]
                     {
                         HayateLogLevel.Trace, HayateLogLevel.Debug, HayateLogLevel.Information,
                         HayateLogLevel.Warning, HayateLogLevel.Error, HayateLogLevel.Critical
                     })
            {
                Assert.True(logger.IsEnabled(level), level.ToString());
            }

            Assert.False(logger.IsEnabled(HayateLogLevel.None));
        }

        [Fact]
        public void BaseClass_ShouldLeaveTheAddedMembersAsNoOps()
        {
            var logger = new MinimalLogger();
            IHayateLogger iface = logger;

            // Inherited and not overridden: these must not throw, and must not disturb the one member the
            // derived class did override.
            iface.LogTrace("t");
            iface.LogDebug("d");
            iface.LogWarning("w");
            iface.LogError(new InvalidOperationException("x"), "e");
            iface.LogCritical(new InvalidOperationException("x"), "c");
            iface.LogInformation("i");

            Assert.Equal(1, logger.InformationCalls);

            using var scope = iface.BeginScope("scope {Name}", "s");
            Assert.NotNull(scope);
        }

        [Fact]
        public void DefaultBeginScope_ShouldHandBackTheSharedNullScope()
        {
            var logger = new MinimalLogger();

            // The interface promises a non-null handle, so the default has to be a real object — and a
            // shared one, since a no-op scope has no per-call state.
            Assert.Same(HayateLoggerBase.NullScope, logger.BeginScope("x"));

            HayateLoggerBase.NullScope.Dispose();
            HayateLoggerBase.NullScope.Dispose();
        }

        // ------------------------------------------------------------------ the migration claim

        [Fact]
        public void MigratedImplementation_ShouldStillReceiveInterfaceCalls()
        {
            var logger = new MigratedLogger();
            IHayateLogger iface = logger;

            iface.LogInformation("a");
            iface.LogDebug("b");

            // The migrated class answers through the interface: its overrides are what the interface maps to.
            Assert.Equal(new[] { "INFO a", "DEBUG b" }, logger.Lines);
        }

        [Fact]
        public void HidingAnInheritedMember_ShouldNotReachTheInterfaceMap()
        {
            var logger = new HidingDerivative();
            IHayateLogger iface = logger;

            iface.LogInformation("a");
            logger.LogInformation("b");

            // The interface call lands on the class that *declared* the implementation, and the hiding
            // method is reachable only through the derived type. This is the mechanism behind the next
            // test: with a virtual body on HayateLoggerBase, the base body would sit in the interface map
            // and a migration that forgot `override` would record nothing while still compiling.
            Assert.Equal(new[] { "INFO a", "HIDDEN b" }, logger.Lines);
        }

        [Fact]
        public void BaseClass_MustKeepTheTwoXMembersAbstract_AndTheThreeZeroAdditionsConcrete()
        {
            // A reflection guard, because the thing being relied on is the compiler and there is no runtime
            // state left to observe once the members are abstract. It was measured on 2026-09-23 with the
            // members declared virtual-with-a-body: the one-clause migration compiled, and the migrated
            // class recorded nothing, because its methods hid the base ones instead of overriding them.
            //
            // One assertion covers both halves: the set of abstract members has to be exactly the four 2.x
            // ones. A fifth entry would mean 3.0 added a member a migration has to answer for; a missing
            // one would mean a virtual body is back, and with it the silent-drop shape measured above.
            var abstractMembers = typeof(HayateLoggerBase)
                .GetMethods()
                .Where(m => m.IsAbstract)
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "LogDebug", "LogError", "LogInformation", "LogWarning" }, abstractMembers);
        }

        // ------------------------------------------------------------------ the IsEnabled gate

        [Fact]
        public void IsEnabledFalse_ShouldSuppressTheDebugTrace_WithoutSilencingAnythingElse()
        {
            var logger = new DebugSuppressedLogger();

            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("l7-gate")
                .WithLogger(logger)
                .WithMinSize(2)
                .WithMaxSize(8)
                .WithEnableAutoScaling(false)
                .WithEnableEviction(false)
                .Build();

            for (var i = 0; i < 8; i++)
            {
                var o = pool.Acquire();
                pool.Release(o);
            }

            // The debug channel never reached the logger: the gate is upstream of the call.
            Assert.Equal(0, logger.DebugCalls);

            // Everything else still arrives — the gate is per level, not a mute.
            Assert.True(logger.InformationCalls > 0, "lifecycle information entries must still arrive");
        }

        [Fact]
        public void IsEnabledTrue_ShouldLetTheDebugTraceThrough()
        {
            // The other arm of the gate: same pool, same traffic, a logger that answers true.
            var logger = new OpenDebugLogger();

            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("l7-gate-open")
                .WithLogger(logger)
                .WithMinSize(2)
                .WithMaxSize(8)
                .WithEnableAutoScaling(false)
                .WithEnableEviction(false)
                .Build();

            for (var i = 0; i < 8; i++)
            {
                var o = pool.Acquire();
                pool.Release(o);
            }

            Assert.True(logger.DebugCalls > 0, "with the gate open the per-operation trace must arrive");
        }

#if !NETFRAMEWORK && !NETSTANDARD2_0
        [Fact]
        public void IsEnabledGate_ShouldRemoveThePerOperationAllocation()
        {
            // Acceptance 3 of L7: the gate exists so a logger that would discard the entry never pays for
            // the params array and the boxed arguments. Same pool, same traffic, two loggers that differ
            // only in what IsEnabled answers; the gated arm has to allocate less. Measured on the calling
            // thread after warm-up, over enough operations that the per-operation allocation dominates the
            // noise (the trace builds one object[] plus one boxed argument per call, twice per
            // acquire+release pair).
            var gated = MeasureAllocation(new DebugSuppressedLogger());
            var ungated = MeasureAllocation(new OpenDebugLogger());

            Assert.True(gated < ungated,
                $"gated={gated} B, ungated={ungated} B — the IsEnabled gate must remove the params array");
        }

        private static long MeasureAllocation(IHayateLogger logger)
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithPoolName("l7-alloc")
                .WithLogger(logger)
                .WithMinSize(4)
                .WithMaxSize(16)
                .WithEnableAutoScaling(false)
                .WithEnableEviction(false)
                .Build();

            // Warm up: the first acquires create the objects and grow the pool, which allocates once.
            for (var i = 0; i < 64; i++)
            {
                var w = pool.Acquire();
                pool.Release(w);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                var w = pool.Acquire();
                pool.Release(w);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
#endif
    }
}

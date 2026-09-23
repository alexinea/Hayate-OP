#pragma warning disable CS0618 // The subject of this file is the obsolete shell; the warning is expected here.

using DotNetCore.HayateOP.Logging;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// The 2.x call-site spelling, compiled as it was (L8). <see cref="LoggingNamespaceMoveTests"/> reaches the
/// obsolete shell by reflection, which proves the type exists; this file proves the part a consumer
/// actually experiences — that the old <c>using</c> plus an unqualified name still compiles and still runs.
/// The file-level suppression of the obsolete warning is the one deliberate difference from what a 2.x
/// project would see.
/// </summary>
public class LegacyLoggingNamespaceCallSiteTests
{
    public sealed class TestObject { }

    /// <summary>Collects what MEL was asked to write, so that "it ran" can be told from "it was a no-op".</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly LogLevel _minimum;

        public CapturingProvider(LogLevel minimum = LogLevel.Trace) => _minimum = minimum;

        public readonly List<string> Messages = new();

        public ILogger CreateLogger(string categoryName) => new Sink(this, _minimum);

        public void Dispose() { }

        private sealed class Sink : ILogger
        {
            private readonly CapturingProvider _owner;
            private readonly LogLevel _minimum;

            public Sink(CapturingProvider owner, LogLevel minimum) => (_owner, _minimum) = (owner, minimum);

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Nop();

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter) => _owner.Messages.Add(formatter(state, exception));
        }

        private sealed class Nop : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void OldNamespace_ShouldStillConstructBothTypesAndLog()
    {
        var capture = new CapturingProvider();
        using var melFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));

        // The 2.x spelling, unchanged.
        var adapter = new HayateMicrosoftLoggerAdapter(melFactory.CreateLogger("pool"));
        IHayateLogger logger = adapter;

        logger.LogInformation("2.x call site {N}", 1);
        logger.LogDebug("still here {N}", 2);

        var factory = new HayateMicrosoftLoggerFactory(melFactory);

        Assert.NotNull(factory.CreateLogger("pool"));
        Assert.Equal(new[] { "2.x call site 1", "still here 2" }, capture.Messages);
    }

    [Fact]
    public void OldNamespaceGenericAdapter_ShouldStillConstruct()
    {
        var capture = new CapturingProvider();
        using var melFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));

        IHayateLogger logger = new HayateMicrosoftLoggerAdapter<TestObject>(
            melFactory.CreateLogger<TestObject>());

        logger.LogWarning("generic 2.x call site {N}", 1);

        Assert.Equal(new[] { "generic 2.x call site 1" }, capture.Messages);
    }

    [Fact]
    public void OldNamespaceShell_ShouldCarryTheMovedImplementation()
    {
        // Compiling is not enough on its own: the shell has to behave, and it does so through the base
        // class's members rather than through anything it declares itself.
        // The sink is level-filtered on purpose, so that the assertions below are about the adapter
        // forwarding the question — and casting HayateLogLevel to LogLevel rather than mapping it — instead
        // of about a sink that answers true to everything. A bare factory would be worse still: MEL answers
        // false for every level when there is no provider at all, which would make them vacuous.
        var capture = new CapturingProvider(LogLevel.Information);
        using var melFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var adapter = new HayateMicrosoftLoggerAdapter(melFactory.CreateLogger("pool"));

        Assert.False(adapter.IsEnabled(HayateLogLevel.Debug));
        Assert.True(adapter.IsEnabled(HayateLogLevel.Information));
        Assert.True(adapter.IsEnabled(HayateLogLevel.Critical));

        using var scope = adapter.BeginScope("scope {N}", 1);
        Assert.NotNull(scope);
    }
}

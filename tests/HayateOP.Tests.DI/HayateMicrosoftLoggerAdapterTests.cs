using DotNetCore.HayateOP.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// The Microsoft.Extensions.Logging bridge, directly (L5): a null sink, the category it writes under, and
/// the exception it carries. The adapter had no tests of its own — it was only ever exercised indirectly,
/// through a container that happened to have logging registered, so all three of these could regress
/// without anything failing.
/// </summary>
public class HayateMicrosoftLoggerAdapterTests
{
    public sealed class TestObject { }

    /// <summary>Records what MEL was actually asked to write, including the category and the exception.</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<string> Categories = new();
        public readonly List<Exception?> Exceptions = new();
        public readonly List<string> Messages = new();

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose() { }

        private sealed class Sink : ILogger
        {
            private readonly CapturingProvider _owner;
            private readonly string _category;

            public Sink(CapturingProvider owner, string category) => (_owner, _category) = (owner, category);

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Nop();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _owner.Categories.Add(_category);
                _owner.Exceptions.Add(exception);
                _owner.Messages.Add(formatter(state, exception));
            }

            private sealed class Nop : IDisposable
            {
                public void Dispose() { }
            }
        }
    }

    private static (DotNetCore.HayateOP.Logging.IHayateLogger Logger, CapturingProvider Capture) BuildCaptured(string category)
    {
        var capture = new CapturingProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));

        return (new HayateMicrosoftLoggerFactory(factory).CreateLogger(category), capture);
    }

    [Fact]
    public void NullLogger_ShouldDiscardEverythingWithoutThrowing()
    {
        DotNetCore.HayateOP.Logging.IHayateLogger logger = new HayateMicrosoftLoggerAdapter(null);

        logger.LogDebug("d {A}", 1);
        logger.LogInformation("i {A}", 1);
        logger.LogWarning("w {A}", 1);
        logger.LogError(new InvalidOperationException("x"), "e {A}", 1);

        // The point of the case is that none of the four threw on a null sink — including LogError, which
        // is the one that carries an exception and is called from the pool's failure paths.
    }

    [Fact]
    public void Logger_ShouldWriteUnderTheCategoryTheFactoryWasAskedFor()
    {
        var (logger, capture) = BuildCaptured("MyConnection:primary");

        logger.LogInformation("Pool configuration reloaded. Type: {Type} NewConfig: {Config}", "MyConnection", 42);

        Assert.Equal(new[] { "MyConnection:primary" }, capture.Categories);
        Assert.Equal("Pool configuration reloaded. Type: MyConnection NewConfig: 42", Assert.Single(capture.Messages));
    }

    [Fact]
    public void LogError_ShouldHandTheExceptionToTheSinkUnchanged()
    {
        var (logger, capture) = BuildCaptured("pool");
        var thrown = new InvalidOperationException("the dependency failed");

        logger.LogError(thrown, "borrow failed for {Pool}", "pool");

        Assert.Same(thrown, Assert.Single(capture.Exceptions));
    }

    [Fact]
    public void LogError_WithoutAnException_ShouldStillReachTheSink()
    {
        var (logger, capture) = BuildCaptured("pool");

        logger.LogError(null!, "no exception here: {N}", 1);

        Assert.Null(Assert.Single(capture.Exceptions));
        Assert.Equal("no exception here: 1", Assert.Single(capture.Messages));
    }
}

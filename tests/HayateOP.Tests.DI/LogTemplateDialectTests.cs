using DotNetCore.HayateOP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// Log template dialect (L4): the pool's templates are read by Microsoft.Extensions.Logging, not by
/// Serilog, so a Serilog-only operator in one of them is not an operator at all — it is part of the
/// property name.
/// </summary>
/// <remarks>
/// <para>
/// <c>{@Config}</c> is Serilog's destructuring operator: in a Serilog template it means "capture this
/// object's members instead of calling ToString() on it". This message never reaches a Serilog template
/// parser — it goes through MEL, which copies the hole verbatim into a structured property named
/// <c>@Config</c>. So the operator does nothing and the name is wrong: a sink filtering on <c>Config</c>
/// finds nothing, and what arrives is a scalar named <c>@Config</c> rather than the object it is.
/// </para>
/// <para>
/// Measured, because it is easy to get this wrong by reasoning alone: the *rendered text* is byte-identical
/// between <c>{Config}</c> and <c>{@Config}</c> — MEL substitutes positionally and calls ToString() either
/// way. Only the structured property name differs, which is why the assertion here is on the property names
/// and not on the message.
/// </para>
/// </remarks>
public class LogTemplateDialectTests
{
    public sealed class TestObject { }

    private sealed class KeyCapturingProvider : ILoggerProvider
    {
        public readonly List<string> Keys = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Keys);

        public void Dispose() { }

        private sealed class Sink : ILogger
        {
            private readonly List<string> _keys;

            public Sink(List<string> keys) => _keys = keys;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Nop();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs) _keys.Add(pair.Key);
                }
            }

            private sealed class Nop : IDisposable
            {
                public void Dispose() { }
            }
        }
    }

    [Fact]
    public void ReloadLog_ShouldNameTheConfigPropertyWithoutTheSerilogOperator()
    {
        var capture = new KeyCapturingProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();

        pool.ReloadConfig(o => o.MinPoolSize = 1);

        Assert.Contains("Config", capture.Keys);
        Assert.DoesNotContain("@Config", capture.Keys);
    }
}

using DotNetCore.HayateOP.Logging;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Per-pool independent LoggerFactory (WithLoggerFactory).
/// Zero-breakage points: when no factory is specified, the built-in singleton logger is used; an explicit WithLogger takes precedence over the factory.
/// </summary>
public class LoggerFactoryTests
{
    private class TestObject { }

    /// <summary>In-memory logger that records all log text.</summary>
    // 3.0 (L7): IHayateLogger gained LogTrace / LogCritical / IsEnabled / BeginScope. Deriving from
    // HayateLoggerBase keeps this double to the four levels it actually records; the added members
    // are inherited and do nothing. See HayateLoggerContractTests for the migration itself.
    private sealed class RecordingLogger : HayateLoggerBase
    {
        public List<string> Lines { get; } = new();

        public override void LogInformation(string message, params object[] args) => Lines.Add(Render("INFO", message, args));
        public override void LogWarning(string message, params object[] args) => Lines.Add(Render("WARN", message, args));
        public override void LogError(Exception ex, string message, params object[] args) => Lines.Add(Render("ERROR", message, args));
        public override void LogDebug(string message, params object[] args) => Lines.Add(Render("DEBUG", message, args));

        private static string Render(string level, string message, object[] args)
        {
            // Same shape as DefaultHayateLogger.RenderTemplate: substitute arguments in the order their placeholders appear
            var sb = new System.Text.StringBuilder(message.Length + 32);
            var argIndex = 0;
            for (var i = 0; i < message.Length; i++)
            {
                if (message[i] == '{' && argIndex < args.Length)
                {
                    var close = message.IndexOf('}', i + 1);
                    if (close > i + 1)
                    {
                        sb.Append(args[argIndex++]?.ToString() ?? "null");
                        i = close;
                        continue;
                    }
                }

                sb.Append(message[i]);
            }

            return $"[{level}] {sb}";
        }
    }

    /// <summary>Recording factory that dispatches by category name.</summary>
    private sealed class RecordingLoggerFactory : IHayateLoggerFactory
    {
        public List<string> Categories { get; } = new();
        public Dictionary<string, RecordingLogger> Loggers { get; } = new();

        public IHayateLogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            var logger = new RecordingLogger();
            Loggers[categoryName] = logger;
            return logger;
        }
    }

    [Fact]
    public void WithLoggerFactory_ShouldCreateDistinctLoggerPerPool()
    {
        var factory = new RecordingLoggerFactory();

        using var poolA = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-alpha")
            .WithLoggerFactory(factory)
            .WithMinSize(2)
            .WithMaxSize(8)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        using var poolB = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-beta")
            .WithLoggerFactory(factory)
            .WithMinSize(2)
            .WithMaxSize(8)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        // The factory creates one logger per pool name, and the two pools get distinct instances
        Assert.Equal(new[] { "pool-alpha", "pool-beta" }, factory.Categories);
        var loggerA = Assert.IsType<RecordingLogger>(factory.Loggers["pool-alpha"]);
        var loggerB = Assert.IsType<RecordingLogger>(factory.Loggers["pool-beta"]);
        Assert.NotSame(loggerA, loggerB);

        // Logs are routed per pool: each shows only its own pool name
        Assert.Contains(loggerA.Lines, l => l.Contains("pool-alpha"));
        Assert.DoesNotContain(loggerA.Lines, l => l.Contains("pool-beta"));
        Assert.Contains(loggerB.Lines, l => l.Contains("pool-beta"));
        Assert.DoesNotContain(loggerB.Lines, l => l.Contains("pool-alpha"));

        // the pool itself is usable
        var obj = poolA.Acquire();
        poolA.Release(obj);
    }

    [Fact]
    public void WithoutLoggerFactory_ShouldKeepBuiltInSingletonBehaviour()
    {
        // Zero breakage: building and using are unaffected when no factory is specified
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-default-logger")
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        // Min=1 warms up 1 object (after shards are evenly split each shard has capacity 1); the build is unaffected by the factory default
        Assert.True(pool.GetStats().CurrentSize >= 1);
    }

    [Fact]
    public void WithLogger_ShouldTakePrecedenceOverFactory()
    {
        var factory = new RecordingLoggerFactory();
        var explicitLogger = new RecordingLogger();

        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-explicit")
            .WithLoggerFactory(factory)
            .WithLogger(explicitLogger)
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        // Explicit logger takes precedence: the factory is not called and logs land on the explicit instance
        Assert.Empty(factory.Categories);
        Assert.Contains(explicitLogger.Lines, l => l.Contains("pool-explicit"));
    }

    [Fact]
    public void WithLoggerFactory_ShouldFailFast_WhenFactoryReturnsNull()
    {
        // Fail fast when the factory returns null (do not silently degrade to no logging)
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-null-logger")
            .WithLoggerFactory(new DelegateHayateLoggerFactory(_ => null!))
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("IHayateLoggerFactory", ex.Message);
    }
}

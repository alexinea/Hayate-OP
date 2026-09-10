using DotNetCore.HayateOP.Logging;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M19：每池独立 LoggerFactory（WithLoggerFactory）。
/// 零破坏要点：未指定工厂时沿用内建单例日志器；显式 WithLogger 优先于工厂。
/// </summary>
public class LoggerFactoryTests
{
    private class TestObject { }

    /// <summary>记录所有日志文本的内存日志器。</summary>
    private sealed class RecordingLogger : IHayateLogger
    {
        public List<string> Lines { get; } = new();

        public void LogInformation(string message, params object[] args) => Lines.Add(Render("INFO", message, args));
        public void LogWarning(string message, params object[] args) => Lines.Add(Render("WARN", message, args));
        public void LogError(Exception ex, string message, params object[] args) => Lines.Add(Render("ERROR", message, args));
        public void LogDebug(string message, params object[] args) => Lines.Add(Render("DEBUG", message, args));

        private static string Render(string level, string message, object[] args)
        {
            // 与 DefaultHayateLogger.RenderTemplate 同构：按占位符出现顺序依次代入参数
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

    /// <summary>按分类名分发的记录型工厂。</summary>
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

        // 工厂按池名各创建一次，且两个池拿到的是不同实例
        Assert.Equal(new[] { "pool-alpha", "pool-beta" }, factory.Categories);
        var loggerA = Assert.IsType<RecordingLogger>(factory.Loggers["pool-alpha"]);
        var loggerB = Assert.IsType<RecordingLogger>(factory.Loggers["pool-beta"]);
        Assert.NotSame(loggerA, loggerB);

        // 日志按池分流：各自只出现自己的池名
        Assert.Contains(loggerA.Lines, l => l.Contains("pool-alpha"));
        Assert.DoesNotContain(loggerA.Lines, l => l.Contains("pool-beta"));
        Assert.Contains(loggerB.Lines, l => l.Contains("pool-beta"));
        Assert.DoesNotContain(loggerB.Lines, l => l.Contains("pool-alpha"));

        // 池本身可用
        var obj = poolA.Acquire();
        poolA.Release(obj);
    }

    [Fact]
    public void WithoutLoggerFactory_ShouldKeepBuiltInSingletonBehaviour()
    {
        // 零破坏：不指定工厂时构建与使用不受影响
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("pool-default-logger")
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        // Min=1 预热 1 个对象（分片均分后每片 1 个容量），构建未受工厂缺省影响
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

        // 显式日志器优先：工厂未被调用，日志落在显式实例上
        Assert.Empty(factory.Categories);
        Assert.Contains(explicitLogger.Lines, l => l.Contains("pool-explicit"));
    }

    [Fact]
    public void WithLoggerFactory_ShouldFailFast_WhenFactoryReturnsNull()
    {
        // 工厂返回 null 时快速失败（不静默降级为无日志）
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

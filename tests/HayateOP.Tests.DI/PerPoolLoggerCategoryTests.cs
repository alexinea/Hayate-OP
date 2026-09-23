using System;
using System.Linq;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// L2 — every pool gets its own Microsoft.Extensions.Logging category, so several pools of one element
/// type can be told apart downstream.
/// </summary>
/// <remarks>
/// Before 2.9 both container paths handed every pool a logger built from
/// <c>CreateLogger&lt;T&gt;()</c>, which is one category shared by the unnamed pool and every named
/// pool of the same type. The category is now the pool name.
/// </remarks>
public class PerPoolLoggerCategoryTests
{
    /// <summary>A logger that records every category it is asked for and writes nothing.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly System.Collections.Generic.List<string> _categories = new();

        public System.Collections.Generic.IReadOnlyList<string> Categories => _categories;

        public ILogger CreateLogger(string categoryName)
        {
            _categories.Add(categoryName);
            return new SilentLogger();
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class SilentLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }

            private sealed class Scope : IDisposable
            {
                public void Dispose() { }
            }
        }
    }

    [Fact]
    public void NamedPools_ShouldEachBeGivenTheirOwnCategory()
    {
        var factory = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddHayatePoolSupport()
            .AddNamedPool<PooledConnection>("primary")
            .AddNamedPool<PooledConnection>("replica");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // The pool is built on first use, which is when its logger is resolved.
        _ = accessor.GetPool<PooledConnection>("primary");
        _ = accessor.GetPool<PooledConnection>("replica");

        Assert.Contains($"{nameof(PooledConnection)}:primary", factory.Categories);
        Assert.Contains($"{nameof(PooledConnection)}:replica", factory.Categories);
        Assert.Equal(2, factory.Categories.Distinct().Count());
    }

    [Fact]
    public void UnnamedPool_ShouldBeGivenTheElementTypeNameAsItsCategory()
    {
        var factory = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddHayatePoolSupport().RegisterHayatePool<PooledConnection>();

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IHayateObjectPool<PooledConnection>>();

        // The pool name, which for the unnamed pool is the element type name. The category used to be
        // MEL's own choice for CreateLogger<T>() — the namespace-qualified display name — so assert
        // that form is gone as well, otherwise this test would pass on either behaviour.
        Assert.Contains(nameof(PooledConnection), factory.Categories);
        Assert.DoesNotContain(typeof(PooledConnection).FullName!, factory.Categories);
    }

    [Fact]
    public void AnExplicitLogger_ShouldStillWinOverTheFactory()
    {
        var factory = new RecordingLoggerFactory();

        using var pool = new HayatePoolBuilder<PooledConnection>()
            .WithLogger(new HayateMicrosoftLoggerAdapter(null))
            .WithLoggerFactory(new HayateMicrosoftLoggerFactory(factory))
            .Build();

        // ResolveLogger consults the factory only when no logger was set explicitly, which is what
        // keeps a caller's own logger authoritative.
        Assert.Empty(factory.Categories);
    }

    [Fact]
    public void WithoutAMelProvider_TheFactoryShouldProduceNoOpLoggers()
    {
        var logger = new HayateMicrosoftLoggerFactory(null).CreateLogger("anything");

        Assert.NotNull(logger);
        logger.LogDebug("no provider configured");
        logger.LogInformation("no provider configured");
        logger.LogWarning("no provider configured");
        logger.LogError(new InvalidOperationException("not thrown"), "no provider configured");
    }
}

/// <summary>A top-level type, so its <see cref="Type.FullName"/> is a plain namespace-qualified name.</summary>
public sealed class PooledConnection { }

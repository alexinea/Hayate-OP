using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.Configuration
{
    /// <summary>
    /// L2 — the configuration path gives every pool its own Microsoft.Extensions.Logging category too.
    /// The container path is covered in <c>HayateOP.Tests.DI</c>; this file exists because the two paths
    /// have separate bridging code and only one of them being switched would be invisible there.
    /// </summary>
    public class PerPoolLoggerCategoryConfigurationTests
    {
        public sealed class PooledConnection { }

        /// <summary>A logger that records every category it is asked for and writes nothing.</summary>
        private sealed class RecordingLoggerFactory : ILoggerFactory
        {
            private readonly List<string> _categories = new List<string>();

            public IReadOnlyList<string> Categories => _categories;

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
        public void AddHayatePoolFromConfiguration_ShouldLogUnderThePoolName()
        {
            var factory = new RecordingLoggerFactory();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "HayatePool:Global:MinPoolSize", "1" },
                    { "HayatePool:Global:MaxPoolSize", "4" }
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(factory);
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<PooledConnection>(config);

            var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IHayateObjectPool<PooledConnection>>();

            Assert.Contains(nameof(PooledConnection), factory.Categories);
            Assert.DoesNotContain(typeof(PooledConnection).FullName!, factory.Categories);
        }
    }
}

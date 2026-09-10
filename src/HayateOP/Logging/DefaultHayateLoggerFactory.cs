using System;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The default logger factory: it ignores the category name and returns the built-in logger equivalent to the one used in 2.4 and earlier.
/// </summary>
public sealed class DefaultHayateLoggerFactory : IHayateLoggerFactory
{
    /// <summary>Shared instance (stateless, safe to reuse).</summary>
    public static readonly DefaultHayateLoggerFactory Instance = new();

    /// <inheritdoc />
    public IHayateLogger CreateLogger(string categoryName) => new DefaultHayateLogger();
}

/// <summary>
/// A delegate-based logger factory: it hands the category name to a user-supplied delegate, making it easy
/// to bridge pool logging into any backend such as MEL <c>ILoggerFactory</c>, Serilog, or NLog.
/// </summary>
public sealed class DelegateHayateLoggerFactory : IHayateLoggerFactory
{
    private readonly Func<string, IHayateLogger> _factory;

    /// <summary>
    /// Creates a delegate-based logger factory.
    /// </summary>
    /// <param name="factory">A mapping delegate from category name to logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    public DelegateHayateLoggerFactory(Func<string, IHayateLogger> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public IHayateLogger CreateLogger(string categoryName)
        => _factory(categoryName) ?? throw new InvalidOperationException(
            "IHayateLoggerFactory returned null; a non-null IHayateLogger is required.");
}

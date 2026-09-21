using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// A logger factory that gives every pool its own Microsoft.Extensions.Logging category.
/// </summary>
/// <remarks>
/// Purpose: the container and configuration paths can register several pools of the same element type —
/// the unnamed pool plus any number of named ones — and before 2.9 they handed every one of them a
/// logger built from <c>CreateLogger&lt;T&gt;()</c>, which is a single category for all of them. Two
/// named pools of <c>MyConnection</c> were therefore indistinguishable downstream: a per-pool log level,
/// a filter or a sink had to be expressed against one shared category string.
/// With this factory the category is the pool name — <c>MyConnection</c> for the unnamed pool and
/// <c>MyConnection:primary</c> / <c>MyConnection:replica</c> for named ones — which is the same name
/// the pool already reports in its own log lines, statistics and snapshots. Serilog, NLog and log4net
/// all reach HayateOP through the Microsoft.Extensions.Logging bridge, so routing MEL by category
/// routes every one of them.
/// Category of the unnamed pool: it used to be Microsoft.Extensions.Logging's own choice for
/// <c>CreateLogger&lt;T&gt;()</c> — the namespace-qualified display name of the element type —
/// and is now the element type name, which is what
/// <see cref="IHayateLoggerFactory.CreateLogger"/> has always documented. A host that configures a log
/// level by the namespace-qualified name has to update that configuration; no code changes are needed.
/// A <c>null</c> factory is accepted and yields no-op loggers, so a host with no logging provider
/// behaves exactly as before. Pass this factory to
/// <c>HayatePoolBuilder&lt;T&gt;.WithLoggerFactory</c>; an explicit <c>WithLogger</c> still wins over it.
/// </remarks>
/// <example>
/// <code>
/// var pool = new HayatePoolBuilder&lt;MyConnection&gt;()
///     .WithPoolName("primary")
///     .WithLoggerFactory(new HayateMicrosoftLoggerFactory(loggerFactory))
///     .Build();
/// </code>
/// </example>
public class HayateMicrosoftLoggerFactory : IHayateLoggerFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Creates a factory over a Microsoft.Extensions.Logging logger factory.
    /// </summary>
    /// <param name="loggerFactory">
    /// The MEL logger factory to create per-category loggers from, or <c>null</c> when the host has no
    /// logging provider — every logger created is then a no-op.
    /// </param>
    public HayateMicrosoftLoggerFactory(ILoggerFactory? loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates the logger for one pool, under the MEL category <paramref name="categoryName"/>.
    /// </summary>
    /// <param name="categoryName">The pool name to log under.</param>
    /// <returns>A logger writing to that category; a no-op logger when no MEL factory was supplied.</returns>
    public IHayateLogger CreateLogger(string categoryName)
        => new HayateMicrosoftLoggerAdapter(_loggerFactory?.CreateLogger(categoryName));
}

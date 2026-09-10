namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The pool-level logger factory.<br />
/// Purpose: creates an independent logger for each object pool by "pool name (category)", replacing the
/// global singleton logger; the typical scenario is routing each pool's logs by name when multiple pools
/// coexist (e.g. to distinct MEL categories / files).
/// </summary>
/// <remarks>
/// Non-breaking contract: when no factory is supplied via
/// <c>HayatePoolBuilder&lt;T&gt;.WithLoggerFactory</c>, behavior is identical to 2.4 and earlier --
/// the Builder's built-in singleton <see cref="IHayateLogger"/> is used. If <c>WithLogger</c> is also
/// called explicitly, the explicit instance takes precedence over the factory.
/// </remarks>
public interface IHayateLoggerFactory
{
    /// <summary>
    /// Creates a logger for the specified logical pool.
    /// </summary>
    /// <param name="categoryName">The log category name (defaults to the pool name from <c>HayatePoolBuilder&lt;T&gt;.WithPoolName</c>, or the element type name if unspecified).</param>
    /// <returns>The logger instance; must not return <c>null</c>.</returns>
    IHayateLogger CreateLogger(string categoryName);
}

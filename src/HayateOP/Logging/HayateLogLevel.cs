namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The severity of a log entry, as far as <see cref="IHayateLogger"/> is concerned.
/// </summary>
/// <remarks>
/// The numeric values are deliberately the ones Microsoft.Extensions.Logging uses for its own
/// <c>LogLevel</c> — Trace 0 through Critical 5, None 6 — so a host that bridges to MEL filters on
/// the ordering it already knows, and the bridge maps a level with a cast instead of a lookup table.
/// <see cref="None"/> is not a level anything is logged at; it is the value
/// <see cref="IHayateLogger.IsEnabled"/> answers <c>false</c> for whatever the implementation filters.
/// </remarks>
public enum HayateLogLevel
{
    /// <summary>The most verbose level, for development-time tracing.</summary>
    Trace = 0,

    /// <summary>Debugging detail. The pool's per-operation borrow and release trace uses this level.</summary>
    Debug = 1,

    /// <summary>Normal lifecycle events: construction, pre-warm, scale-up, eviction.</summary>
    Information = 2,

    /// <summary>A condition that is recoverable but worth attention.</summary>
    Warning = 3,

    /// <summary>An operation failed.</summary>
    Error = 4,

    /// <summary>The pool or the process cannot continue.</summary>
    Critical = 5,

    /// <summary>Not a level: an implementation answers <c>false</c> for this and everything else it filters.</summary>
    None = 6
}

using Microsoft.CodeAnalysis;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// The diagnostic descriptors this package reports.
/// </summary>
/// <remarks>
/// One place for the ids, titles, severities and help links, so a test can read the shipped severity
/// back off the descriptor instead of restating it. The severities here are the contract: <c>HYO009</c>
/// is <see cref="DiagnosticSeverity.Error"/> on purpose — see <c>docs/analyzers.md</c> section 2.1.
/// </remarks>
internal static class HayateDiagnostics
{
    private const string Category = "DotNetCore.HayateOP";

    private const string HelpBase = "https://github.com/alexinea/Hayate-OP/blob/master/docs/analyzers.md";

    /// <summary>A lease or scope is acquired and never disposed (Error).</summary>
    internal static readonly DiagnosticDescriptor LeaseNotDisposed = new DiagnosticDescriptor(
        id: "HYO009",
        title: "A lease or scope is acquired and never disposed",
        messageFormat: "The lease returned by '{0}' is never disposed, so the pooled object is never returned to its pool",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "HayatePoolScope<T> is a sealed class with no finalizer and HayatePoolLease<T> is a struct: neither returns the object unless Dispose or DisposeAsync is called, so a dropped lease loses the object for the life of the process.",
        helpLinkUri: HelpBase + "#hyo009");

    /// <summary>A pooled object is acquired and never released (Warning).</summary>
    internal static readonly DiagnosticDescriptor PooledObjectNotReleased = new DiagnosticDescriptor(
        id: "HYO001",
        title: "A pooled object is acquired and never released",
        messageFormat: "The object returned by '{0}' is never released, so the pool keeps counting it as on loan",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An object acquired from a pool comes back through Release. A borrow whose result is discarded, or stored in a local nobody ever disposes, is indistinguishable from a long-lived borrow and holds the object out of circulation.",
        helpLinkUri: HelpBase + "#hyo001");

    /// <summary>A disposed pool is used again (Warning).</summary>
    internal static readonly DiagnosticDescriptor DisposedPoolUsed = new DiagnosticDescriptor(
        id: "HYO003",
        title: "A disposed pool is used again",
        messageFormat: "'{0}' was disposed earlier in this method and is used again here",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dispose is terminal: the pool's bookkeeping, timers and registry entry are gone. For the keyed pool and the shared registry a later borrow throws ObjectDisposedException; for the ordinary pool it does not throw at all, which is worse.",
        helpLinkUri: HelpBase + "#hyo003");

    /// <summary>A synchronous acquire is used inside an async method (Info).</summary>
    internal static readonly DiagnosticDescriptor SynchronousAcquireInAsync = new DiagnosticDescriptor(
        id: "HYO004",
        title: "A synchronous acquire is used inside an async method",
        messageFormat: "'{0}' blocks the calling thread; inside an async method the awaitable form yields instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A synchronous borrow blocks the caller's thread and, if the borrow is held across an await, keeps an object out of the pool for the duration. AcquireAsync is the shape that yields. Advisory: a borrow released before the first await is legitimate, and the analyzer cannot see whether the borrow crosses an await.",
        helpLinkUri: HelpBase + "#hyo004");

    /// <summary>An async acquire does not forward the available cancellation token (Info).</summary>
    internal static readonly DiagnosticDescriptor CancellationTokenNotForwarded = new DiagnosticDescriptor(
        id: "HYO005",
        title: "An async acquire does not forward the available cancellation token",
        messageFormat: "'{0}' is called without a cancellation token, though '{1}' is in scope",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "When the enclosing method already has a CancellationToken and it is not forwarded, a caller that gives up still waits for the object: the cancellation stops propagating at this call.",
        helpLinkUri: HelpBase + "#hyo005");

    /// <summary>OnRelease disposes the item instead of releasing it (Warning).</summary>
    internal static readonly DiagnosticDescriptor OnReleaseDisposesItem = new DiagnosticDescriptor(
        id: "HYO006",
        title: "OnRelease disposes the item instead of releasing it",
        messageFormat: "'{0}' disposes the item in OnRelease; teardown belongs in OnDestroy",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "OnRelease answers whether the object may be parked. Disposing the item there destroys an object the pool still owns and will hand to the next borrower; the pool calls OnDestroy when it destroys an object for good.",
        helpLinkUri: HelpBase + "#hyo006");
}

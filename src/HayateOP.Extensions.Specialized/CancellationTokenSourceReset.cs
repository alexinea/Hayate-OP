using System;
using System.Reflection;
using System.Threading;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// Locates <c>CancellationTokenSource.TryReset()</c> once and caches it as a delegate, so the pool can be
/// written the same way for every target framework it ships to.
/// </summary>
/// <remarks>
/// <c>TryReset()</c> is the only way to make a returned <see cref="CancellationTokenSource"/> fit for an
/// unrelated rental: it disarms the timer the previous renter may have started, drops the callbacks it
/// registered, and refuses to reset a source whose cancellation has already been requested. The method
/// does not exist on .NET Framework 4.8, and rather than fork the pool's source per target the lookup is
/// done at run time: where the method is missing the lookup yields <c>null</c>, <see cref="TryReset"/>
/// answers <c>false</c>, and the pool destroys a returned source instead of parking it — a source the pool
/// cannot clean is a source it must not hand out again, because the previous renter's timer and callbacks
/// would otherwise fire during the next rental.<br />
/// The lookup runs once per process, on the first use of a pool, and its result is cached as an open
/// delegate, so a later return pays a delegate call rather than a reflection call. A trimmed or
/// native-AOT application may have removed the method from the metadata; the lookup then yields
/// <c>null</c> and the pool degrades exactly as it does on .NET Framework — still correct, only without
/// reuse.
/// </remarks>
internal static class CancellationTokenSourceReset
{
    /// <summary>
    /// The cached <c>TryReset()</c> call, or <c>null</c> when this runtime does not have one.
    /// </summary>
    private static readonly Func<CancellationTokenSource, bool>? s_tryReset = LocateTryReset();

    /// <summary>
    /// Whether this runtime's <see cref="CancellationTokenSource"/> can be reset and reused at all.
    /// </summary>
    internal static bool IsSupported => s_tryReset is not null;

    /// <summary>
    /// Attempts to reset <paramref name="source"/> so it can serve an unrelated rental.
    /// </summary>
    /// <param name="source">The source being returned to the pool.</param>
    /// <returns><c>true</c> when the source is clean and may be parked; <c>false</c> when this runtime
    /// cannot reset one at all, or when the source has already been canceled or has a cancellation in
    /// flight.</returns>
    internal static bool TryReset(CancellationTokenSource source)
        => s_tryReset is not null && s_tryReset(source);

    private static Func<CancellationTokenSource, bool>? LocateTryReset()
    {
        MethodInfo? method;
        try
        {
            method = typeof(CancellationTokenSource).GetMethod(
                "TryReset",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
        }
        catch (Exception)
        {
            // A runtime that refuses the lookup — a partially trimmed assembly can — is treated as one
            // without TryReset. The pool degrades; it does not fail.
            return null;
        }

        if (method is null || method.ReturnType != typeof(bool) || method.ContainsGenericParameters)
        {
            return null;
        }

        try
        {
            // An open delegate over the instance: the call site pays one indirect call, not a reflection
            // invocation.
            return method.CreateDelegate(typeof(Func<CancellationTokenSource, bool>)) as Func<CancellationTokenSource, bool>;
        }
        catch (Exception)
        {
            // A member of the right name but the wrong shape is not ours to call; degrade as if absent.
            return null;
        }
    }
}

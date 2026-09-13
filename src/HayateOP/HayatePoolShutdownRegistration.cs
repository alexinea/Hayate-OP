using System;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// One pool's subscription to a <see cref="IHayateShutdownHook"/>, held so the pool can detach itself on
/// disposal.
/// </summary>
/// <remarks>
/// Detaching is the point of this class. The process-wide hook lives as long as the process, so a
/// subscription that outlives its pool keeps that pool — and everything it holds — reachable for the rest
/// of the process lifetime. <see cref="Dispose"/> is therefore idempotent: whichever happens first, the
/// explicit disposal or the shutdown call, and however many of them happen, exactly one unregistration
/// takes place.
/// </remarks>
internal sealed class HayatePoolShutdownRegistration : IDisposable
{
    private readonly IHayateShutdownHook _hook;
    private readonly Action _handler;

    // 0 = subscribed; 1 = detached.
    private int _detached;

    private HayatePoolShutdownRegistration(IHayateShutdownHook hook, Action handler)
    {
        _hook = hook;
        _handler = handler;
        hook.Register(handler);
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the supplied hook, falling back to the process-wide hook
    /// when none is given.
    /// </summary>
    /// <param name="hook">The hook to subscribe to; when <c>null</c>, <see cref="HayateProcessShutdownHook"/>
    /// is used.</param>
    /// <param name="handler">The shutdown callback.</param>
    /// <returns>The subscription, which detaches on disposal.</returns>
    internal static HayatePoolShutdownRegistration Register(IHayateShutdownHook? hook, Action handler)
    {
        return new HayatePoolShutdownRegistration(hook ?? new HayateProcessShutdownHook(), handler);
    }

    /// <summary>Removes the subscription once, whatever route the disposal took.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;
        _hook.Unregister(_handler);
    }
}

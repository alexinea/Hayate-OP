using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// The event source behind process shutdown, so a pool can release itself when the process goes away.
/// </summary>
/// <remarks>
/// A pool normally relies on its owner calling its <c>Dispose</c> method; when the owner is
/// the process itself there is no such call, because nothing runs at that point. This interface supplies
/// the notification instead, and lets a host plug in its own shutdown signal — an application lifetime,
/// a container, or a test — rather than forcing the process-wide one.
/// </remarks>
/// <example>
/// <code>
/// pool = new HayatePoolBuilder&lt;MyResource&gt;()
///     .WithAutoDisposeWithSystem()
///     .Build();   // disposed automatically when the process exits
/// </code>
/// </example>
public interface IHayateShutdownHook
{
    /// <summary>
    /// Subscribes <paramref name="handler"/> to the shutdown notification.
    /// </summary>
    /// <param name="handler">The callback invoked on shutdown; must not be <c>null</c>.</param>
    /// <remarks>
    /// Registering the same handler twice subscribes it once. Implementations must be safe to call from
    /// several threads, because pools register and unregister as they are built and disposed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    void Register(Action handler);

    /// <summary>
    /// Removes <paramref name="handler"/> from the shutdown notification.
    /// </summary>
    /// <param name="handler">A handler previously passed to <see cref="Register"/>.</param>
    /// <remarks>
    /// Unregistering a handler that is not registered does nothing. Removing the subscription is what
    /// stops the hook — which for the process-wide implementation lives as long as the process — from
    /// keeping a disposed pool reachable.
    /// </remarks>
    void Unregister(Action handler);
}

/// <summary>
/// The default <see cref="IHayateShutdownHook"/>: process exit and a terminal Ctrl+C.
/// </summary>
/// <remarks>
/// Both notifications are subscribed, because either one can end a process: <c>ProcessExit</c> covers
/// ordinary termination, and <c>CancelKeyPress</c> covers a console application stopped from the
/// keyboard. Neither is cancelled by this hook — termination proceeds, the pool merely gets cleaned up on
/// the way out.<br />
/// One instance serves any number of handlers: each registration keeps its own adapter delegates, which
/// is what makes a later <see cref="Unregister"/> able to remove exactly one pool's subscription from the
/// process-wide events. Handlers run in subscription order, each shielded from the others, so one pool's
/// failed disposal cannot abandon the pools behind it — swallowing is the only option at this point,
/// because there is no caller left to observe the failure.<br />
/// Whatever work the handlers do shares the constraint of the events themselves: at process exit no
/// other thread is guaranteed to be idle, so disposal happens without any guarantee that no borrow is
/// still in flight.
/// </remarks>
/// <example>
/// <code>
/// var hook = new HayateProcessShutdownHook();
/// hook.Register(() =&gt; Console.WriteLine("exiting"));
/// </code>
/// </example>
public sealed class HayateProcessShutdownHook : IHayateShutdownHook
{
    private sealed class Subscription
    {
        private readonly HayateProcessShutdownHook _owner;

        internal Subscription(HayateProcessShutdownHook owner, Action handler)
        {
            _owner = owner;
            Handler = handler;
            ProcessExitHandler = (_, _) => owner.InvokeHandlers();
            CancelKeyPressHandler = (_, _) => owner.InvokeHandlers();
        }

        internal Action Handler { get; }

        /// <summary>The adapted <c>ProcessExit</c> delegate — the identity needed to unsubscribe.</summary>
        internal EventHandler ProcessExitHandler { get; }

        /// <summary>The adapted <c>CancelKeyPress</c> delegate — the identity needed to unsubscribe.</summary>
        internal ConsoleCancelEventHandler CancelKeyPressHandler { get; }
    }

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();

    /// <inheritdoc />
    public void Register(Action handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        lock (_gate)
        {
            foreach (var existing in _subscriptions)
            {
                if (ReferenceEquals(existing.Handler, handler)) return;   // already subscribed: nothing to do
            }

            var subscription = new Subscription(this, handler);
            AppDomain.CurrentDomain.ProcessExit += subscription.ProcessExitHandler;
            Console.CancelKeyPress += subscription.CancelKeyPressHandler;
            _subscriptions.Add(subscription);
        }
    }

    /// <inheritdoc />
    public void Unregister(Action handler)
    {
        lock (_gate)
        {
            for (var i = _subscriptions.Count - 1; i >= 0; i--)
            {
                var subscription = _subscriptions[i];
                if (!ReferenceEquals(subscription.Handler, handler)) continue;

                AppDomain.CurrentDomain.ProcessExit -= subscription.ProcessExitHandler;
                Console.CancelKeyPress -= subscription.CancelKeyPressHandler;
                _subscriptions.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Runs every subscribed handler, shielding each one so that a failure cannot stop handlers
    /// subscribed after it.
    /// </summary>
    private void InvokeHandlers()
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = _subscriptions.ToArray();
        }

        foreach (var subscription in snapshot)
        {
            try
            {
                subscription.Handler();
            }
            catch
            {
                // Shutdown is not a place where a failure can be reported: there is no caller to observe
                // it, and letting it escape would abandon every handler behind this one. It is therefore
                // contained here, per handler, rather than propagated.
            }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// An asynchronous preparation strategy (N3), aligned with marklauter's
/// <c>IPreparationStrategy</c>: every borrow can check whether the object is actually ready to use
/// and — when it is not — repair it asynchronously before the caller sees it, which is the classic
/// reconnect flow of a connection pool (SMTP, database, message broker).
/// </summary>
/// <remarks>
/// Install a strategy with <c>pool.WithPreparation(strategy)</c>
/// (see <see cref="HayatePreparationPoolExtensions"/>); the returned
/// <see cref="HayatePreparationPool{T}"/> wraps any <see cref="IHayateObjectPool{T}"/> — the bounded
/// engine, the lean fast path, keyed sub-pools, or <see cref="HayateUnboundedPool{T}"/> — and runs
/// the check/prepare chain on every borrow. The six synchronous policy hooks are unchanged; this
/// interface exists precisely because "reconnect the connection" cannot be expressed in them.<br />
/// The methods are plain <c>Task</c>-based (not value-task based) so the
/// interface is available on every target framework, including net48 and netstandard2.0.
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
public interface IHayatePreparationStrategy<T> where T : class
{
    /// <summary>
    /// Reports whether <paramref name="item"/> is ready to be handed to a borrower right now (for a
    /// connection: still connected and responsive).
    /// </summary>
    /// <param name="item">The object just taken from the pool.</param>
    /// <param name="cancellationToken">Propagated from the borrow call.</param>
    /// <returns><c>true</c> to deliver the object as-is; <c>false</c> to run
    /// <see cref="PrepareAsync"/> first.</returns>
    Task<bool> IsReadyAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares <paramref name="item"/> for the borrower — typically the asynchronous reconnect or
    /// re-authentication of a dropped connection.
    /// </summary>
    /// <param name="item">The object that reported not-ready.</param>
    /// <param name="cancellationToken">Propagated from the borrow call.</param>
    /// <exception cref="Exception">A failure means the object could not be repaired; the
    /// preparation pool destroys it and retries with another (up to the configured attempt
    /// limit).</exception>
    Task PrepareAsync(T item, CancellationToken cancellationToken = default);
}

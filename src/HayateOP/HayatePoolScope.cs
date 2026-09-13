using System;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// A borrow that returns itself: the object taken from the pool is held by this lease, and disposing the
/// lease puts it back exactly once. It exists so a borrow/return pair can be written with <c>using</c>
/// instead of a hand-written <c>try</c>/<c>finally</c> — the return happens even when the body throws.
/// </summary>
/// <remarks>
/// Obtain one through <see cref="HayatePoolScopeExtensions"/> — the four <c>AcquireScoped</c> /
/// <c>AcquireScopeAsync</c> overloads are the only producers. Nothing else produces a lease, so the
/// lifetime is unambiguous: the caller owns it, and the object it holds is unusable once the lease ends.<br />
/// Disposal is idempotent. A lease represents one borrow, so returning must happen exactly once — a second
/// <see cref="Dispose"/> call is deliberately a no-op rather than a second return, because double-returning
/// the same object would let two borrowers share it later. The single-call guarantee holds even if several
/// threads dispose the same lease concurrently, through an atomic claim.<br />
/// A lease is a small object; taking one allocates what a hand-written <c>try</c>/<c>finally</c> would not.
/// That is the deliberate trade: the cost is one allocation per borrow in exchange for never leaking a
/// borrowed object from an exception path.
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <example>
/// <code>
/// using var lease = pool.AcquireScoped();
/// lease.Value.DoWork();     // returned to the pool at the end of the enclosing block
/// </code>
/// </example>
public sealed class HayatePoolScope<T> : IDisposable where T : class
{
    // 0 = the lease is live and owns the borrowed object; 1 = the object has been returned. The claim is
    // atomic so concurrent disposals collapse into one return, and a lease whose body threw can still be
    // safely disposed by `using`.
    private int _returned;

    private IHayateObjectPool<T>? _pool;
    private T? _value;

    internal HayatePoolScope(IHayateObjectPool<T> pool, T value)
    {
        _pool = pool;
        _value = value;
    }

    /// <summary>
    /// The borrowed object.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed — the object has already been
    /// returned to the pool and must not be touched again.</exception>
    /// <example>
    /// <code>
    /// using var lease = pool.AcquireScoped();
    /// Console.WriteLine(lease.Value);
    /// </code>
    /// </example>
    public T Value
    {
        get
        {
            var value = _value;
            if (value is null)
            {
                throw new ObjectDisposedException(nameof(HayatePoolScope<T>),
                    "The lease has ended and its object has already been returned to the pool.");
            }

            return value;
        }
    }

    /// <summary>
    /// Whether this lease still owns its object (equivalently: whether it has not been disposed yet).
    /// </summary>
    /// <remarks>
    /// Useful for handlers that receive a lease they may or may not be responsible for, where calling
    /// <see cref="Dispose"/> unconditionally is easier than tracking ownership. It is a plain read of
    /// volatile-visible state, so it is advisory — <see cref="Dispose"/> remains the authoritative
    /// once-only return even when this says <c>true</c>.
    /// </remarks>
    public bool IsActive => _value is not null;

    /// <summary>
    /// Returns the borrowed object to the pool. Called automatically at the end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The return happens exactly once; every later call is a no-op. After disposal, reading
    /// <see cref="Value"/> throws and <see cref="IsActive"/> is <c>false</c>. The release itself goes
    /// through the pool's ordinary return path, so validation, the policy hooks and background eviction
    /// behave exactly as they do for a manual <c>Release</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var lease = pool.AcquireScoped();
    /// try { /* use lease.Value */ }
    /// finally { lease.Dispose(); }
    /// </code>
    /// </example>
    public void Dispose()
    {
        // Atomic claim: only the caller that flips the flag from 0 to 1 performs the return. Everything
        // needed for that return is captured into locals first, so the losers observe a settled state.
        if (Interlocked.Exchange(ref _returned, 1) != 0) return;

        var pool = _pool;
        var value = _value;
        _pool = null;
        _value = null;

        if (pool is not null && value is not null)
        {
            pool.Release(value);
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// A borrow that returns itself, as a value type: the object taken from the pool is held by this lease, and
/// disposing the lease puts it back exactly once. It is the allocation-free counterpart of
/// <see cref="HayatePoolScope{T}"/> — same lifetime rules, same return path, no heap object.
/// </summary>
/// <remarks>
/// <para>
/// Obtain one through <see cref="HayatePoolLeaseExtensions"/> — the four <c>AcquireLeased</c> /
/// <c>AcquireLeaseAsync</c> overloads are the only producers. Nothing else produces a lease, so the lifetime
/// is unambiguous: the caller owns it, and the object it holds is unusable once the lease ends.
/// </para>
/// <para>
/// <b>Which scope should I use?</b> Both return the object on every path, including the exception paths, and
/// both run the identical return path (validation, policy hooks, background eviction). They differ in one
/// respect: this lease is a value type kept on the stack or in the calling frame, so acquiring one does not
/// allocate, while <see cref="HayatePoolScope{T}"/> is a class and costs one allocation per borrow. Reach for
/// this lease on hot paths and in loops; reach for the scope when you need to pass the lease to a method that
/// outlives the frame, store it in a field, hand it across an async boundary as an object, or otherwise need
/// to treat it as a reference.
/// </para>
/// <para>
/// Disposal is idempotent. A lease represents one borrow, so returning must happen exactly once — a second
/// <see cref="Dispose"/> call is deliberately a no-op rather than a second return, because double-returning
/// the same object would let two borrowers share it later. The single-call guarantee holds even if several
/// threads dispose the same lease concurrently, through an atomic claim.
/// </para>
/// <para>
/// <b>Why this is a mutable struct rather than a <c>ref struct</c>.</b> A <c>ref struct</c> cannot live across
/// an <c>await</c> boundary: the compiler rejects it (CS4007), because the borrowed object would then have no
/// defined owner while the continuation is pending. That makes its asynchronous disposal unreachable in
/// practice, which would be a capability <em>regression</em> against <see cref="HayatePoolScope{T}"/>, whose
/// asynchronous disposal the 2.9 work added. Keeping the type a plain mutable struct preserves every capability
/// of the scope — including <c>await using</c> and borrows that span an <c>await</c> — while still removing the
/// per-borrow allocation. The price is the one guarantee a <c>ref struct</c> gave for free: this lease
/// <i>can</i> be boxed, so putting one in a field or an <c>object</c> reference is allowed by the compiler and
/// allocates then. It stays a value type in the normal <c>using</c> / <c>await using</c> flow. A
/// <c>readonly struct</c> would not work either: the once-only return claim needs
/// <see cref="Interlocked.Exchange(ref int, int)"/>, and taking a field of a <c>readonly struct</c> by reference
/// is rejected (CS8340).
/// </para>
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <example>
/// <code>
/// using var lease = pool.AcquireLeased();
/// lease.Value.DoWork();     // returned to the pool at the end of the enclosing block
///
/// await using var asyncLease = await pool.AcquireLeaseAsync(cancellationToken);
/// asyncLease.Value.DoWork();
/// </code>
/// </example>
public struct HayatePoolLease<T> : IDisposable
#if NET6_0_OR_GREATER
    , IAsyncDisposable
#endif
    where T : class
{
    // 0 = the lease is live and owns the borrowed object; 1 = the object has been returned. The claim is
    // atomic so concurrent disposals collapse into one return, and a lease whose body threw can still be
    // safely disposed by `using`. This field is why the struct is not `readonly` (see the remarks).
    private int _returned;

    // Cleared on the return path so `Value` can tell a live lease from a released one, and so a released
    // lease holds nothing that could keep the pooled object alive.
    private T? _value;

    private IHayateObjectPool<T>? _pool;

    internal HayatePoolLease(IHayateObjectPool<T> pool, T value)
    {
        _pool = pool;
        _value = value;
        _returned = 0;
    }

    /// <summary>
    /// The borrowed object.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed — the object has already been
    /// returned to the pool and must not be touched again.</exception>
    /// <example>
    /// <code>
    /// using var lease = pool.AcquireLeased();
    /// Console.WriteLine(lease.Value);
    /// </code>
    /// </example>
    public T Value
    {
        get
        {
            var value = _value;
            if (value is null || Volatile.Read(ref _returned) != 0)
            {
                // Thrown from a factory method rather than a void throw helper: the compiler needs to see the
                // `throw` itself to know this branch never yields a value (CS8603), and the helper keeps the
                // cold path out of the inlined getter.
                throw CreateDisposedException();
            }

            return value;
        }
    }

    /// <summary>
    /// Whether this lease still owns its object (equivalently: whether it has not been disposed yet).
    /// </summary>
    /// <remarks>
    /// Useful for handlers that receive a lease they may or may not be responsible for, where calling
    /// <see cref="Dispose"/> unconditionally is easier than tracking ownership. It is a plain read, so it is
    /// advisory — <see cref="Dispose"/> remains the authoritative once-only return even when this says
    /// <c>true</c>.
    /// </remarks>
    public bool IsActive => _value is not null && Volatile.Read(ref _returned) == 0;

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
    /// var lease = pool.AcquireLeased();
    /// try { /* use lease.Value */ }
    /// finally { lease.Dispose(); }
    /// </code>
    /// </example>
    public void Dispose()
    {
        if (TryClaimReturn(out var pool, out var value))
        {
            pool.Release(value);
        }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Returns the borrowed object asynchronously. Called automatically at the end of an <c>await using</c> block.
    /// </summary>
    /// <remarks>
    /// For a pool with an asynchronous return path, completion means its asynchronous policy hooks have finished.
    /// Other pool implementations use their ordinary synchronous return path.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (!TryClaimReturn(out var pool, out var value)) return default;

        if (pool is IHayateAsyncReturnPool<T> asyncPool)
        {
            return asyncPool.ReleaseAsync(value);
        }

        pool.Release(value);
        return default;
    }
#endif

    private bool TryClaimReturn(out IHayateObjectPool<T> pool, out T value)
    {
        pool = null!;
        value = null!;
        if (Interlocked.Exchange(ref _returned, 1) != 0) return false;

        var claimedPool = _pool;
        var claimedValue = _value;

        if (claimedPool is null || claimedValue is null) return false;

        // `Interlocked.Exchange` above is the authoritative claim: any Dispose that gets past it owns the
        // return, so the fields are cleared here to drop the reference to the pooled object. A concurrent
        // reader of `Value` is unaffected either way — it observes either the live object or the null, and the
        // null is the answer it must give once the return has been claimed.
        _pool = null;
        _value = null;

        pool = claimedPool;
        value = claimedValue;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ObjectDisposedException CreateDisposedException()
        => new ObjectDisposedException(
            "HayatePoolLease<" + typeof(T).Name + ">",
            "The lease has ended and its object has already been returned to the pool.");
}

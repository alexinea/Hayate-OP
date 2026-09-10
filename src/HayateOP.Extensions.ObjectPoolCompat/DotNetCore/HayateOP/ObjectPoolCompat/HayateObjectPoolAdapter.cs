using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;
using System;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// Wraps a HayateOP pool as an <see cref="ObjectPool{T}"/> (the Microsoft.Extensions.ObjectPool
/// abstract base class), letting existing MEOP callers (which only depend on
/// <c>ObjectPool&lt;T&gt;</c>'s Get/Return) switch to HayateOP with zero code changes.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// Semantic mapping: <c>Get()</c> → <see cref="IHayateObjectPool{T}.Acquire()"/>;
/// <c>Return(obj)</c> → <see cref="IHayateObjectPool{T}.Release"/>.
/// On rejection/overflow HayateOP destroys the object inside Release, matching the MEOP
/// "discard when full" semantics.
/// Note: the blocking/creation behavior of Get is determined by the wrapped pool's own rejection
/// policy — for MEOP semantics (synchronously create immediately on an empty pool) build the pool
/// via <see cref="HayateObjectPoolCompatProvider"/>.
/// </remarks>
public sealed class HayateObjectPoolAdapter<T> : ObjectPool<T>, IDisposable where T : class
{
    /// <summary>The wrapped HayateOP pool instance.</summary>
    public IHayateObjectPool<T> InnerPool { get; }

    /// <summary>
    /// Wraps an already-built HayateOP pool.
    /// </summary>
    /// <param name="innerPool">The HayateOP pool to wrap. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerPool"/> is null.</exception>
    public HayateObjectPoolAdapter(IHayateObjectPool<T> innerPool)
    {
        InnerPool = innerPool ?? throw new ArgumentNullException(nameof(innerPool));
    }

    /// <inheritdoc />
    public override T Get() => InnerPool.Acquire();

    /// <inheritdoc />
    public override void Return(T obj) => InnerPool.Release(obj);

    /// <summary>Disposes the underlying HayateOP pool.</summary>
    public void Dispose() => InnerPool.Dispose();
}

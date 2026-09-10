using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.ObjectPool;
using System;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// Adapts the Microsoft.Extensions.ObjectPool <see cref="IPooledObjectPolicy{T}"/> to HayateOP's
/// <see cref="IHayateObjectPolicy{T}"/> so that existing policy implementations drive HayateOP
/// pools unchanged.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// Hook mapping (MEOP has only two hooks; the rest map to no-ops):
/// <list type="bullet">
/// <item><c>Create()</c> → <see cref="IPooledObjectPolicy{T}.Create"/></item>
/// <item><c>OnRelease(item)</c> → <see cref="IPooledObjectPolicy{T}.Return"/>
/// (returning false rejects the object, which HayateOP destroys — consistent with the MEOP "discard" semantics)</item>
/// <item><c>Validate</c> → always true (MEOP has no borrow-time validation; IResettable.TryReset
/// is handled internally by the default MEOP policy and naturally reaches the Return hook)</item>
/// <item><c>OnAcquire/OnPassivate/OnDestroy</c> → no-op</item>
/// </list>
/// </remarks>
public sealed class HayateCompatPooledObjectPolicy<T> : IHayateObjectPolicy<T> where T : class
{
    private readonly IPooledObjectPolicy<T> _inner;

    /// <summary>
    /// Drives the adapter using the existing MEOP policy.
    /// </summary>
    /// <param name="inner">The MEOP pooled object policy to adapt. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public HayateCompatPooledObjectPolicy(IPooledObjectPolicy<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public T Create() => _inner.Create();

    /// <inheritdoc />
    public bool OnRelease(T item) => _inner.Return(item);

    /// <inheritdoc />
    public bool Validate(T item) => true;

    /// <inheritdoc />
    public void OnAcquire(T item) { }

    /// <inheritdoc />
    public void OnPassivate(T item) { }

    /// <inheritdoc />
    public void OnDestroy(T item) { }
}

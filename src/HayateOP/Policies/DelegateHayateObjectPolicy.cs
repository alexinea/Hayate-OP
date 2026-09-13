using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP;

/// <summary>
/// An <see cref="IHayateObjectPolicy{T}"/> built from delegates. <see cref="HayatePool.Simple{T}"/> uses it
/// so creation can be expressed as a lambda instead of a policy class.
/// </summary>
/// <remarks>
/// Everything the policy does not customize keeps the neutral behaviour that makes it safe as a default:
/// objects are accepted back unconditionally and always count as valid — per-object validity is the caller's
/// own responsibility through the pool's own validation switches. Destruction of <c>IDisposable</c> objects
/// stays with the pool, so this policy does not dispose anything of its own accord.
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
internal sealed class DelegateHayateObjectPolicy<T> : IHayateObjectPolicy<T> where T : class
{
    private readonly Func<T> _create;
    private readonly Action<T>? _onAcquire;

    internal DelegateHayateObjectPolicy(Func<T> create, Action<T>? onAcquire = null)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _onAcquire = onAcquire;
    }

    /// <summary>Creates a new object by invoking the supplied factory.</summary>
    public T Create()
    {
        var item = _create();
        if (item is null)
        {
            // A factory returning null would leave the pool holding a null instance, which every borrow
            // path then has to defend against. Failing at the creation boundary keeps that invariant local.
            throw new InvalidOperationException("The HayatePool object factory returned null.");
        }

        return item;
    }

    /// <summary>Accepts the object back into the pool.</summary>
    public bool OnRelease(T item) => true;

    /// <summary>Considers the object valid; per-object validity is left to the pool's own switches.</summary>
    public bool Validate(T item) => true;

    /// <summary>Invokes the acquire callback supplied at construction, if any.</summary>
    public void OnAcquire(T item) => _onAcquire?.Invoke(item);

    /// <summary>Does nothing; returning an object to the idle state needs no preparation here.</summary>
    public void OnPassivate(T item)
    {
    }

    /// <summary>Does nothing; disposing <c>IDisposable</c> objects is performed by the pool itself.</summary>
    public void OnDestroy(T item)
    {
    }
}

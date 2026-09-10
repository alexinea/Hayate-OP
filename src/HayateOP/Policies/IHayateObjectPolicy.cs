namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// Defines the policy that controls how the pool creates, validates, and disposes pooled objects.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
public interface IHayateObjectPolicy<T> where T : class
{
    /// <summary>Creates a new pooled object.</summary>
    /// <returns>A new instance of <typeparamref name="T"/>; must not be <c>null</c>.</returns>
    T Create();
    /// <summary>Called when an object is returned to the pool.</summary>
    /// <param name="item">The object being returned.</param>
    /// <returns><c>true</c> to accept the object back into the pool; <c>false</c> to destroy it.</returns>
    bool OnRelease(T item);
    /// <summary>Validates whether an object is still usable before it is handed out or returned.</summary>
    /// <param name="item">The object to validate.</param>
    /// <returns><c>true</c> if the object is valid; <c>false</c> to destroy it.</returns>
    bool Validate(T item);
    /// <summary>Called when an object is acquired (borrowed) from the pool.</summary>
    /// <param name="item">The object being acquired.</param>
    void OnAcquire(T item);
    /// <summary>Called just before an object is placed back into the pool after return.</summary>
    /// <param name="item">The object about to be passivated.</param>
    void OnPassivate(T item);
    /// <summary>Called when an object is destroyed (removed from the pool for good).</summary>
    /// <param name="item">The object being destroyed.</param>
    void OnDestroy(T item);
}
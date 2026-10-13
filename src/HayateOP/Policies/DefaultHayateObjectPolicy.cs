namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// The default object-pool policy.
/// </summary>
/// <remarks>
/// <para>
/// Objects are created with <c>new T()</c>, validity follows <see cref="IHayateValidatable"/>, and an
/// object implementing <see cref="IHayateResettable"/> is reset — but <i>when</i> it is reset is a choice,
/// fixed at construction. The parameterless constructor keeps the historical timing (reset on release);
/// the two-flag constructor takes the timing from
/// <see cref="HayatePoolOptions.ResetOnReturn"/> / <see cref="HayatePoolOptions.ResetOnBorrow"/>, which is
/// what <see cref="HayatePoolBuilder{T}"/> passes when it resolves the default policy for a pool.
/// </para>
/// <para>
/// The flags are read-only, so a policy's timing cannot change underneath a running pool. Both may be set,
/// which resets on both sides of the cycle; neither is set only if the caller asks for that explicitly, and
/// then the pooled type is simply never reset by this policy.
/// </para>
/// </remarks>
/// <typeparam name="T"></typeparam>
public class DefaultHayateObjectPolicy<T> : IHayateObjectPolicy<T> where T : class, new()
{
    private readonly bool _resetOnReturn;

    private readonly bool _resetOnBorrow;

    /// <summary>
    /// Creates the policy with its historical reset timing: an <see cref="IHayateResettable"/> object is
    /// reset when it is released, and the borrow-side hook does nothing.
    /// </summary>
    public DefaultHayateObjectPolicy()
        : this(true, false)
    {
    }

    /// <summary>
    /// Creates the policy with an explicit reset timing.
    /// </summary>
    /// <param name="resetOnReturn">Whether <see cref="OnRelease"/> resets an
    /// <see cref="IHayateResettable"/> object.</param>
    /// <param name="resetOnBorrow">Whether <see cref="OnAcquire"/> resets an
    /// <see cref="IHayateResettable"/> object.</param>
    public DefaultHayateObjectPolicy(bool resetOnReturn, bool resetOnBorrow)
    {
        _resetOnReturn = resetOnReturn;
        _resetOnBorrow = resetOnBorrow;
    }

    public T Create() => new T();

    public bool OnRelease(T item)
    {
        if (_resetOnReturn && item is IHayateResettable r) r.Reset();
        return true;
    }

    public bool Validate(T item)
    {
        if (item is IHayateValidatable v) return v.IsValid();
        return true;
    }

    public void OnAcquire(T item)
    {
        // Off by default, so for the historical timing this is one predicted-false branch on the borrow
        // path and nothing else.
        if (_resetOnBorrow && item is IHayateResettable r) r.Reset();
    }

    public void OnPassivate(T item)
    {
    }

    public void OnDestroy(T item)
    {
    }
}

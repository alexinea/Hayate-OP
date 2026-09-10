namespace DotNetCore.HayateOP;

/// <summary>
/// Marks an object whose state can be automatically reset when it is returned to the pool.
/// </summary>
public interface IHayateResettable
{
    /// <summary>Resets the object to a reusable initial state before it is handed back to a caller.</summary>
    void Reset();
}
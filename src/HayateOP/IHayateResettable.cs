namespace DotNetCore.HayateOP;

/// <summary>
/// Marks an object whose state the pool can reset automatically, so a borrower never sees what the
/// previous one left behind.
/// </summary>
/// <remarks>
/// <i>When</i> the reset runs is the pool's choice, not the marker's. The default policy resets on
/// release, and <see cref="HayatePoolOptions.ResetOnReturn"/> / <see cref="HayatePoolOptions.ResetOnBorrow"/>
/// move that (3.1, G-7); <see cref="HayateUnboundedPool{T}"/> always resets on release, because it takes
/// no options object for the switches to travel in. An implementation must therefore be safe to call
/// from either side of the cycle, and must not assume it runs exactly once per borrow — both switches
/// may be on at once, which resets twice.
/// </remarks>
public interface IHayateResettable
{
    /// <summary>Restores the object to a reusable initial state.</summary>
    void Reset();
}

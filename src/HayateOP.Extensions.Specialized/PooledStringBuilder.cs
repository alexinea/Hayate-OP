using System;
using System.Text;
using System.Threading;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A <see cref="StringBuilder"/> holder that knows the pool it came from: disposing it returns it to that
/// pool instead of dropping it, so a borrow/build pair can be written with <c>using</c>.
/// </summary>
/// <remarks>
/// While the instance is registered with a <see cref="StringBuilderPool"/>, <see cref="Dispose"/> hands it
/// back through the pool's ordinary return path — validation and the clear behave exactly as for a manual
/// <c>Release</c>. The instance itself is kept alive for the next borrower; only the pool can destroy it,
/// and it does so when the builder grew past the pool's maximum capacity.<br />
/// Disposal is once-per-borrow: a second <see cref="Dispose"/> of the same borrow is a no-op rather than
/// a duplicate return. After disposing, the instance must not be used again — if it was already returned
/// and re-borrowed by someone else, disposing it a second time would be a foreign return.<br />
/// An instance created directly (never handed out by a pool) owns nobody: <see cref="Dispose"/> does
/// nothing and the garbage collector reclaims it.
/// </remarks>
/// <example>
/// <code>
/// using var sb = StringBuilderPool.Instance.GetObject();
/// sb.StringBuilder.Append("hello");
/// var text = sb.ToString();   // returned to the pool at the end of the block
/// </code>
/// </example>
public sealed class PooledStringBuilder : IDisposable
{
    // The pool that created (and therefore owns) this instance; null once the pool destroys the instance
    // or when it was created standalone. Volatile so a disposing thread never routes a return through a
    // stale owner after the pool has detached the instance.
    private volatile IHayateObjectPool<PooledStringBuilder>? _owner;

    // 1 = the instance sits in the pool, 0 = a borrower holds it. The CAS from 0 to 1 in Dispose is the
    // once-per-borrow guard: only the first dispose of a borrow performs the return.
    private int _idle = 1;

    /// <summary>
    /// Creates a standalone instance whose builder starts with the given capacity.
    /// </summary>
    /// <param name="capacity">The initial capacity of the builder.</param>
    /// <remarks>
    /// A standalone instance is not registered with any pool; disposing it does nothing. Only
    /// <see cref="StringBuilderPool"/> produces pooled instances.
    /// </remarks>
    public PooledStringBuilder(int capacity)
    {
        StringBuilder = new StringBuilder(capacity);
    }

    /// <summary>
    /// Creates a standalone instance whose builder starts with no reserved capacity.
    /// </summary>
    /// <remarks>
    /// The parameterless constructor exists so generic pool frameworks that demand a default
    /// constructor can handle the type (HayateOP's <see cref="HayatePoolBuilder{T}"/> requires one);
    /// <see cref="StringBuilderPool"/> uses it and then reserves the configured capacity.
    /// </remarks>
    public PooledStringBuilder()
    {
        StringBuilder = new StringBuilder();
    }

    /// <summary>
    /// The builder.
    /// </summary>
    public StringBuilder StringBuilder { get; }

    /// <summary>
    /// Returns the builder's current content, as P89OP's wrapper does.
    /// </summary>
    public override string ToString() => StringBuilder.ToString();

    /// <summary>
    /// Converts the builder's content to a string and returns the builder to its pool in one call —
    /// CPL's <c>ToStringReturn</c>: the conversion and the return are one operation, so the borrow ends
    /// exactly where the text is produced.
    /// </summary>
    /// <returns>The builder's current content.</returns>
    /// <remarks>
    /// The return is atomic with the conversion: it runs even when the conversion throws, so a failing
    /// path can never leak the builder out of the pool. Like <see cref="Dispose"/>, the return happens
    /// exactly once per borrow — a second call on an already-returned builder is a no-op return that
    /// observes whatever the next borrower may have written, so treat the call as the end of the borrow,
    /// exactly as the end of a <c>using</c> block.<br />
    /// On a standalone instance (never handed out by a pool) the conversion runs and the return step
    /// does nothing.
    /// </remarks>
    /// <example>
    /// <code>
    /// var sb = StringBuilderPool.Instance.GetObject();
    /// sb.StringBuilder.Append("order: ").Append(42);
    /// var text = sb.ToStringReturn();   // builder returned to the pool here
    /// </code>
    /// </example>
    public string ToStringReturn()
    {
        try
        {
            return StringBuilder.ToString();
        }
        finally
        {
            // The conversion owns the borrow's outcome: whether it produced the string or threw, the
            // builder goes back. Dispose is the exactly-once return path, identical to a using block.
            Dispose();
        }
    }

    /// <summary>
    /// Disposing a pooled instance returns it to its pool; disposing a standalone instance does nothing.
    /// Called automatically at the end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The return goes through the pool's ordinary return path, so capacity validation and the clear
    /// behave exactly as for a manual <c>Release</c> — and a builder that grew past the pool's maximum
    /// capacity is destroyed here instead of being parked. The return happens exactly once per borrow;
    /// every later call is a no-op.
    /// </remarks>
    public void Dispose()
    {
        var owner = _owner;
        if (owner is null)
        {
            // Standalone or detached by the pool: nothing to return, the GC reclaims the instance.
            return;
        }

        // Once-per-borrow claim: the first dispose flips the state and returns the instance, later
        // disposals of the same borrow observe the flipped state and do nothing.
        if (Interlocked.CompareExchange(ref _idle, 1, 0) == 0)
        {
            owner.Release(this);
        }
    }

    /// <summary>Registers the owning pool; called by the pool's policy at creation time.</summary>
    internal void BindOwner(IHayateObjectPool<PooledStringBuilder> owner) => _owner = owner;

    /// <summary>
    /// The pool this instance currently belongs to (its lending engine pool, or the wrapper pool until
    /// the policy binds the engine); <c>null</c> for standalone or pool-destroyed instances. The pool
    /// routes a manual <c>Release</c> through it so a borrowed builder returns to the tier it came from.
    /// </summary>
    internal IHayateObjectPool<PooledStringBuilder>? HomePool => _owner;

    /// <summary>Marks the instance as borrowed so the next dispose performs the return.</summary>
    internal void OnBorrowed() => Interlocked.Exchange(ref _idle, 0);

    /// <summary>
    /// Marks the instance as idle; called by the pool's policy on every successful return, including a
    /// manual <c>Release</c>, so a dispose after a manual release cannot route a second return.
    /// </summary>
    internal void MarkIdle() => Interlocked.Exchange(ref _idle, 1);

    /// <summary>
    /// Detaches the pool before the pool destroys the instance, so nothing routes a return for a
    /// destroyed instance.
    /// </summary>
    internal void OnDetachedFromPool() => _owner = null;
}

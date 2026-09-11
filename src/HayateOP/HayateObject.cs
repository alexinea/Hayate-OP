using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// Describes where a pooled wrapper currently lives.
/// It drives the atomic claim protocol of <see cref="HayatePoolBasic{T}.Shard"/>:
/// only the caller that wins the <c>InPool -> Removing</c> transition is allowed
/// to destroy the object, which eliminates the "destroy while borrowed" race.
/// </summary>
internal enum HayateObjectLocation
{
    /// <summary>Just created, not owned by any shard yet.</summary>
    None = 0,

    /// <summary>Sitting idle inside a shard free list.</summary>
    InPool = 1,

    /// <summary>Handed out to a caller.</summary>
    Borrowed = 2,

    /// <summary>Claimed by eviction / idle validation, awaiting destruction.</summary>
    Removing = 3,

    /// <summary>Already destroyed; must never be handed out again.</summary>
    Destroyed = 4
}

public class HayateObject<T> where T : class
{
    public T Value { get; set; }

    /// <summary>
    /// Timestamp sourced from <see cref="Stopwatch.GetTimestamp"/> instead of <c>DateTime</c>,
    /// eliminating the 5–10× syscall overhead of <see cref="DateTime.UtcNow"/> on hot paths
    /// (creation / borrow / return / eviction checks).
    /// To convert to a duration: <c>(end - start) / Stopwatch.Frequency</c> (seconds) or
    /// <c>(end - start) * 1000.0 / Stopwatch.Frequency</c> (milliseconds).
    /// </summary>
    public long CreatedAt { get; set; }

    /// <inheritdoc cref="CreatedAt"/>
    public long LastBorrowedAt { get; set; }

    /// <inheritdoc cref="CreatedAt"/>
    public long LastReleasedAt { get; set; }

    /// <summary>
    /// Wall-clock timestamp captured at creation time (<c>DateTimeOffset.UtcNow.Ticks</c>).
    /// It complements <see cref="CreatedAt"/> (Stopwatch ticks, used for duration measurement); this
    /// field is for human-readable snapshot / log output and never participates in any duration calculation.
    /// </summary>
    public long CreatedAtTick { get; set; }

    /// <summary>
    /// Total number of times this object has been leased (borrowed). Monotonically increasing on the
    /// borrow path (the wrapper is exclusively owned by the borrowing thread at that instant, so eviction /
    /// validation cannot claim a borrowed object -- a plain increment suffices, no Interlocked needed).
    /// </summary>
    public int LeaseCount { get; internal set; }

    /// <summary>
    /// The logical name of the owning pool (written by the pool at creation, immutable for the object's
    /// lifetime). Used by cross-pool diagnostics / snapshot output to locate object ownership.
    /// </summary>
    public string OwnerPoolName { get; set; }

    /// <summary>
    /// Borrow lease context (lease id + borrow-call stack frames + borrow timestamp).
    /// Captured via the AsyncLocal async flow of <see cref="HayateLeaseContext"/> plus this property
    /// (wrapper-side snapshot reference). Concurrent borrow/return each hold independent context instances
    /// and no longer overwrite each other. The capture mode and sampling frequency are still controlled by
    /// <see cref="HayateLeakTraceCaptureMode"/>.
    /// An earlier revision briefly introduced <c>AcquireStackFrames: StackFrame[]</c>; this property is its
    /// final form. The pre-2.5 <c>AcquireTrace: string</c> has been removed; obtain the text form via
    /// <c>TakeSnapshot().LeakTraces</c>.
    /// </summary>
    public HayateLeaseContext LeaseContext { get; internal set; }

    /// <summary>
    /// Whether the object is currently borrowed, derived as a computed property of <see cref="Location"/>
    /// to eliminate the dual-source inconsistency window. The original separate bool field and Location
    /// (Borrowed state) were maintained by two paths, risking stale reads under the weak memory model;
    /// Location is volatile and all transitions happen inside the Shard spin lock, so it is the single
    /// source of truth. Removing (eviction claim) / Destroyed are not Borrowed, consistent with the
    /// original field's semantics.
    /// </summary>
    public bool IsBorrowed => Location == HayateObjectLocation.Borrowed;
    public int Generation { get; set; } // 0 = young generation, 1 = old generation

    public int ValidationSkipCount { get; set; } // validation-skip count accumulated in the old generation

    public long LeaseTimeMs { get; set; }

    /// <summary>
    /// The shard index the object belonged to when borrowed.
    /// Recorded by <see cref="HayatePoolBasic{T}"/> when Acquire hits, and used by Release to round-trip
    /// back to the same shard, avoiding the bug where <c>Thread.GetCurrentProcessorId() % ShardCount</c>
    /// could land on the max=0 shard and silently dispose the object.
    /// The default value 0 is the legitimate home for a single PreWarm object.
    /// </summary>
    public int ShardIndex { get; set; }

    /// <summary>
    /// The object's current location, driving the shard-side atomic claim protocol.
    /// All state transitions happen inside the Shard spin lock, so here only <c>volatile</c> is needed
    /// to guarantee cross-lock visibility.
    /// </summary>
    internal volatile HayateObjectLocation Location;

    /// <summary>
    /// The node reference of the object within its shard's free list; <c>null</c> means it is not in any
    /// shard list. May only be read/written inside the Shard spin lock; it downgrades Remove from an O(n)
    /// queue rebuild to an O(1) unlink.
    /// </summary>
    internal LinkedListNode<HayateObject<T>> Node;

    /// <summary>
    /// Destruction idempotency flag (0 = not destroyed, 1 = destroyed). Eviction, idle validation, and
    /// return-rejection may concurrently hit the same object, so a CAS guarantees it is destroyed only once.
    /// </summary>
    internal int Destroyed;

    /// <summary>
    /// Intrusive singly-linked link used by the per-shard spare-wrapper stack. When a wrapper is
    /// destroyed, the pool parks it on its home shard's spare stack (bounded by the shard's max size)
    /// so a future object creation can reuse it instead of allocating a new wrapper. While parked,
    /// <see cref="Value"/> is <c>null</c> (cleared by the destroy path) so the stack never keeps a
    /// destroyed pooled object alive. Only the pool's create/destroy paths touch this field.
    /// </summary>
    internal HayateObject<T> SpareNext;

    /// <summary>
    /// Resets every per-lease and per-object field so this wrapper can safely wrap a freshly created
    /// pooled value (wrapper recycling). The resulting state is identical to a brand-new wrapper
    /// produced by the constructor: lease bookkeeping, generations, timestamps and the shard-claim
    /// protocol state all start from zero, and no reference to the previous pooled value is retained.
    /// Called by the pool right after a spare wrapper is taken from the spare stack, before the new
    /// value becomes observable — spare wrappers are consumed nowhere else.
    /// </summary>
    /// <param name="newValue">The freshly created pooled object to wrap; must not be <c>null</c>.</param>
    /// <param name="ownerPoolName">The logical name of the owning pool.</param>
    /// <param name="shardIndex">The index of the shard that will own the wrapped object.</param>
    internal void PrepareForRecycle(T newValue, string ownerPoolName, int shardIndex)
    {
        Value = newValue;
        CreatedAt = Stopwatch.GetTimestamp();
        LastBorrowedAt = 0;
        LastReleasedAt = CreatedAt;
        // Wall-clock creation time (snapshot output only; not used for duration calculation).
        CreatedAtTick = DateTimeOffset.UtcNow.Ticks;
        LeaseCount = 0;
        OwnerPoolName = ownerPoolName;
        LeaseContext = null;
        Generation = 0;
        ValidationSkipCount = 0;
        LeaseTimeMs = 0;
        ShardIndex = shardIndex;
        Node = null;
        Location = HayateObjectLocation.None;
        Interlocked.Exchange(ref Destroyed, 0);
        SpareNext = null;
    }

    /// <summary>
    /// Initializes a new wrapper around the supplied pooled value.
    /// </summary>
    /// <param name="value">The pooled object to wrap; must not be <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
    public HayateObject(T value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        CreatedAt = Stopwatch.GetTimestamp();
        LastReleasedAt = CreatedAt;
        // Wall-clock creation time (snapshot output only; not used for duration calculation).
        CreatedAtTick = DateTimeOffset.UtcNow.Ticks;
    }
}

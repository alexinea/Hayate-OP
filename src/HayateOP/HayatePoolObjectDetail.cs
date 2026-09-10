namespace DotNetCore.HayateOP;

/// <summary>
/// Per-object lifecycle detail within a snapshot (2.5).
/// Carried by <see cref="HayatePoolSnapshot.ObjectDetails"/>; one snapshot covers every live wrapper object
/// (idle plus borrowed). Fields are captured at the snapshot instant and carry no consistency guarantee (diagnostic use only).
/// </summary>
public sealed class HayatePoolObjectDetail
{
    /// <summary>The index of the shard that owns this object.</summary>
    public int ShardIndex { get; set; }

    /// <summary>Whether the object is borrowed at the snapshot instant.</summary>
    public bool IsBorrowed { get; set; }

    /// <summary>The cumulative number of times this object has been borrowed.</summary>
    public int LeaseCount { get; set; }

    /// <summary>The wall-clock timestamp at creation time (same basis as <c>DateTimeOffset.UtcNow.Ticks</c>).</summary>
    public long CreatedAtTick { get; set; }

    /// <summary>The duration of the most recent lease, in milliseconds (the last recorded value, or 0 if not yet returned).</summary>
    public long LeaseTimeMs { get; set; }

    /// <summary>The generation marker (0 = young generation, 1 = old generation; promoted only when generational optimization is enabled).</summary>
    public int Generation { get; set; }

    /// <summary>The logical name of the owning pool.</summary>
    public string OwnerPoolName { get; set; }
}

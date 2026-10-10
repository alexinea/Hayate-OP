namespace DotNetCore.HayateOP.Samples.Sharding;

/// <summary>
/// A trivial pooled payload used to show how the pool spreads objects across shards.
/// </summary>
/// <remarks>
/// The type carries no real behavior. The Sharding sample never touches the object's contents; it only
/// inspects the pool's diagnostic snapshot to see which shard each live wrapper landed on. A plain data
/// holder with a stable identity plus a label is therefore enough to make the distribution observable.
/// </remarks>
internal sealed class PooledItem
{
    /// <summary>A stable identity assigned at creation, used only for printing and diagnostics.</summary>
    public int Id { get; init; }

    /// <summary>An arbitrary label describing the object; left as an empty string by default.</summary>
    public string Tag { get; set; } = string.Empty;
}

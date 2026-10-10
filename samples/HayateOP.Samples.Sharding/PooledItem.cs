namespace DotNetCore.HayateOP.Samples.Sharding;

/// <summary>
/// A trivial pooled payload used to show how the pool spreads objects across shards.
/// </summary>
internal sealed class PooledItem
{
    public int Id { get; init; }
    public string Tag { get; set; } = string.Empty;
}

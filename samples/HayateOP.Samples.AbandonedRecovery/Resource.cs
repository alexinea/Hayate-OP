using System.Threading;

namespace DotNetCore.HayateOP.Samples.AbandonedRecovery;

/// <summary>
/// A trivial pooled type. The auto-assigned <see cref="Id"/> is only used for illustrative logging.
/// </summary>
internal sealed class Resource
{
    private static int _nextId;

    /// <summary>Unique, stable identity assigned at construction time.</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);
}

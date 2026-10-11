using System.Threading;

namespace DotNetCore.HayateOP.Samples.Eviction;

/// <summary>
/// Stands in for an expensive-to-create dependency (a DB connection, a socket). The pool tracks how many
/// times each instance has been leased; the custom eviction policy reads that count from the candidate it
/// is handed, so this type itself needs no wear-tracking field.
/// </summary>
internal sealed class Connection
{
    private static int _nextId;

    /// <summary>Unique, stable identity assigned at construction time (used only for logging).</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);
}

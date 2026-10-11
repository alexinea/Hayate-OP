using System.Threading;

namespace DotNetCore.HayateOP.Samples.BorrowOrder;

/// <summary>
/// A trivial pooled type. The auto-assigned <see cref="Id"/> lets the sample prove which physical
/// object a borrow returned, which is what makes the Fifo/Lifo hand-out order observable.
/// </summary>
internal sealed class Widget
{
    private static int _nextId;

    /// <summary>Unique, stable identity assigned at construction time (1, 2, 3, ...).</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>When this widget was created (used only for illustrative logging).</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

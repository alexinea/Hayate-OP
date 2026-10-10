using System.Threading;

namespace DotNetCore.HayateOP.Samples.Scoped;

/// <summary>
/// A trivial pooled payload used by the Scoped sample. The pool constructs instances with the
/// parameterless constructor and hands them out through <see cref="HayatePoolScope{T}"/> leases.
/// </summary>
internal sealed class Resource
{
    // A process-wide counter that hands out a stable, increasing Id to every object the pool builds.
    // The Id is used only to make the sample's console output traceable; it has no effect on pool behavior.
    private static int _counter;

    /// <summary>Monotonic identifier assigned to the resource at construction time (1, 2, 3, ...).</summary>
    /// <remarks>Read-only after construction. Surfaced in <see cref="DoWork"/> output so callers can tell objects apart.</remarks>
    public int Id { get; } = Interlocked.Increment(ref _counter);

    /// <summary>Runs some (pretend) work against the borrowed resource.</summary>
    /// <param name="tag">A label echoed into the output to show which borrow / frame drove the work.</param>
    public void DoWork(string tag) => Console.WriteLine($"   Resource #{Id} working on: {tag}");
}

namespace DotNetCore.HayateOP.Samples.Lease;

/// <summary>
/// A trivial pooled worker used by the Lease sample. It exists only to give the pool a concrete
/// object to hand out, so the sample can focus on how the lease returns it rather than on real work.
/// </summary>
internal sealed class Worker
{
    /// <summary>
    /// A stable identifier assigned once at construction, useful for tracing which pooled instance
    /// performed which task in the sample output.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Prints a line showing this worker is doing work for the given <paramref name="tag"/>.
    /// </summary>
    /// <param name="tag">A caller-supplied label (e.g. "sync", "item-12") identifying the borrow.</param>
    public void DoWork(string tag) => Console.WriteLine($"   Worker#{Id} working on {tag}");
}

namespace DotNetCore.HayateOP.Samples;

/// <summary>
/// The pooled object used by the tour sample. It implements <see cref="IHayateResettable"/> (so the pool
/// can reset it between borrows) and <see cref="IDisposable"/> (so the pool can release any held resources
/// when the object is destroyed). Keeping it trivial lets the tour focus on pool mechanics rather than real work.
/// </summary>
public class MyPooledObject : IHayateResettable, IDisposable
{
    /// <summary>
    /// A caller-assigned identifier, handy for tracing which pooled instance is doing which task.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Arbitrary payload carried by the object between borrows.
    /// </summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="Dispose"/> has run; useful for asserting an object is not used after destruction.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Resets the object's mutable state so the next borrower receives a clean instance.
    /// </summary>
    public void Reset()
    {
        Id = 0;
        Data = string.Empty;
    }

    /// <summary>
    /// Releases any resources held by the object. The tour sample holds none, so this just records disposal.
    /// </summary>
    public void Dispose()
    {
        // Release resources
        IsDisposed = true;
    }
}

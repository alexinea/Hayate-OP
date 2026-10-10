namespace DotNetCore.HayateOP.Samples.Scaling;

/// <summary>
/// A stand-in for a database connection used to demonstrate on-demand pool growth and auto-scaling.
/// </summary>
/// <remarks>
/// The type intentionally has no real connect/disconnect logic: the Scaling sample only creates and
/// counts objects to prove the pool grows and shrinks. The <see cref="IsOpen"/> flag mirrors the kind of
/// per-instance state a real pooled resource would carry.
/// </remarks>
internal sealed class DbConnection
{
    /// <summary>A stable identity assigned at creation, used for diagnostics/printing.</summary>
    public int Id { get; init; }

    /// <summary>Whether the connection is currently considered open; toggled by the consuming code.</summary>
    public bool IsOpen { get; set; }
}

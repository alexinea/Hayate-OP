namespace DotNetCore.HayateOP.Samples.Metrics;

/// <summary>
/// A trivial pooled object used to exercise pool behaviour in the Metrics sample.
/// It carries no real state other than an identifier, so the focus of the sample
/// stays on the pool's observability surface (stats, allocation tracking and capacity
/// alarms) rather than on object construction cost.
/// </summary>
internal sealed class Resource
{
    /// <summary>An integer handle identifying this pooled object. Assigned by the pool factory.</summary>
    /// <remarks>
    /// For this sample the value is informational only (the default policy constructs instances with the
    /// public parameterless constructor and does not populate <see cref="Id"/>), so it defaults to 0.
    /// </remarks>
    public int Id { get; set; }
}

namespace DotNetCore.HayateOP.Samples.Resilience;

/// <summary>
/// A trivial pooled object used to exercise pool availability and backpressure behaviour
/// (circuit breaker + reject policies) in the Resilience sample. It holds no real state,
/// so the demo can concentrate on what the pool does when it is full or taken out of service.
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

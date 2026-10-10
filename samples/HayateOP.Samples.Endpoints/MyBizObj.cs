using DotNetCore.HayateOP;

namespace HayateOP.Samples.Endpoints;

/// <summary>
/// The pooled business object exposed by the Endpoints sample. It implements
/// <see cref="IHayateResettable"/> (the pool resets it between borrows) and
/// <see cref="IHayateValidatable"/> (the pool can refuse to reuse it if <see cref="IsValid"/> returns false),
/// which makes it safe to hand out to concurrent HTTP requests.
/// </summary>
public class MyBizObj : IHayateResettable, IHayateValidatable
{
    /// <summary>
    /// A stable per-instance identifier, assigned once at construction. Useful for correlating a pooled
    /// instance across log lines in the sample's HTTP handlers.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Resets the object's state before it is reused. The Endpoints sample just logs the reset so the
    /// request handler's output is easy to follow.
    /// </summary>
    public void Reset() => Console.WriteLine("Object reset");

    /// <summary>
    /// Reports whether the object is still fit for use. The sample always returns <c>true</c>; returning
    /// <c>false</c> would make the pool destroy the object instead of parking it for reuse.
    /// </summary>
    /// <returns><c>true</c> if the object is valid for reuse.</returns>
    public bool IsValid() => true;
}

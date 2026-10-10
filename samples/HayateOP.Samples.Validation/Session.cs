using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Samples.Validation;

/// <summary>
/// A pooled object modelling a session that can become invalid while checked out, which lets the
/// sample demonstrate the pool's validation and leak-detection features.
/// </summary>
/// <remarks>
/// Implementing <see cref="IHayateValidatable"/> makes <see cref="IsValid"/> the hook the pool calls
/// when <c>WithValidateOnBorrow</c> / <c>WithValidateOnReturn</c> / <c>WithValidateWhileIdle</c> are on.
/// An object that fails the check is destroyed instead of being parked back in the pool.
/// </remarks>
internal sealed class Session : IHayateValidatable
{
    /// <summary>An integer handle identifying this pooled session. Assigned by the pool factory.</summary>
    public int Id { get; set; }

    /// <summary>Whether the session has been closed. A closed session is considered invalid for reuse.</summary>
    public bool Closed { get; private set; }

    /// <summary>Simulates use of the session. Intentionally empty for the sample.</summary>
    public void Use() { }

    /// <summary>Marks the session as closed, which makes <see cref="IsValid"/> return <c>false</c>.</summary>
    /// <remarks>Used by the sample to illustrate that an invalid object returned to the pool is destroyed.</remarks>
    public void Close() => Closed = true;

    /// <summary>Validation hook consulted by the pool: a session is valid only while it is not closed.</summary>
    /// <returns><c>true</c> when the session can still be reused; <c>false</c> once <see cref="Close"/> was called.</returns>
    public bool IsValid() => !Closed;
}

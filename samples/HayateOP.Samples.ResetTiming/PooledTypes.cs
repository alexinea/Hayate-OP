using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Samples.ResetTiming;

/// <summary>
/// A pooled object that records how often the pool reset it and whether a borrower left state in it.
/// <see cref="ResetCount"/> is the observable the sample reads: it distinguishes "reset on the way in"
/// from "reset on the way out" without needing to watch the object at the moment it moves.
/// </summary>
internal sealed class ResettableBuffer : IHayateResettable
{
    /// <summary>How many times the pool has called <see cref="Reset"/> on this instance.</summary>
    public int ResetCount;

    /// <summary>
    /// A stand-in for real state. A borrower sets it to a non-zero value; <see cref="Reset"/> clears it.
    /// Whether the sample sees a stale value on the next borrow is exactly what the timing switches decide.
    /// </summary>
    public int Payload;

    /// <summary>Restores the object to a reusable state and counts the call.</summary>
    public void Reset()
    {
        ResetCount++;
        Payload = 0;
    }
}

/// <summary>
/// A custom policy that resets in its own <c>OnRelease</c>, standing in for any hand-written policy.
/// The reset switches govern the pool-driven <see cref="IHayateResettable"/> reset only, so this policy
/// keeps resetting whatever the options say — the sample asserts that boundary rather than assuming it.
/// </summary>
internal sealed class SelfResettingPolicy : IHayateObjectPolicy<ResettableBuffer>
{
    /// <summary>Creates a fresh buffer.</summary>
    public ResettableBuffer Create() => new ResettableBuffer();

    /// <summary>Resets on release, unconditionally — the policy owns this hook, not the pool.</summary>
    public bool OnRelease(ResettableBuffer item)
    {
        item.Reset();
        return true;
    }

    /// <summary>No validation in this sample.</summary>
    public bool Validate(ResettableBuffer item) => true;

    /// <summary>No acquire-side hook — the policy's reset is on the return side only.</summary>
    public void OnAcquire(ResettableBuffer item) { }

    /// <summary>No passivation.</summary>
    public void OnPassivate(ResettableBuffer item) { }

    /// <summary>No destruction.</summary>
    public void OnDestroy(ResettableBuffer item) { }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// Borrow lease context (2.5, breaking change).
/// <para>
/// One borrow corresponds to one immutable lease context: a process-wide monotonically increasing lease id,
/// a borrow-time timestamp (Stopwatch ticks), and a borrow-frame array. The carrier is <see cref="AsyncLocal{T}"/>
/// (<see cref="Current"/>): the context flows with the caller's asynchronous execution flow (ExecutionContext), and <b>concurrent borrows and returns each hold an independent copy,
/// so they no longer overwrite each other</b> (in 2.4 and earlier, string-based capture on the wrapper object was overwritten by re-borrows of the same object, and could not be attributed correctly across asynchronous
/// flows when shared).
/// </para>
/// <para>
/// Lifecycle: created and written to the current async flow and the wrapper object when Acquire captures evidence; when Release ends the lease,
/// the current async flow is cleared; when Destroy runs, the reference on the wrapper object is cleared. The capture switch and frequency remain controlled by
/// <see cref="HayateLeakTraceCaptureMode"/> (Off disables capture by default; the semantics are unchanged).
/// </para>
/// </summary>
/// <example>
/// <code>
/// var ctx = HayateLeaseContext.Current;
/// if (ctx is not null)
/// {
///     Console.WriteLine($"Lease {ctx.LeaseId} borrowed at tick {ctx.BorrowedAt}");
/// }
/// </code>
/// </example>
public sealed class HayateLeaseContext
{
    // Async-flow carrier. static readonly: each write to AsyncLocal copies the current execution context and
    // overwrites this flow's slot; sibling async flows are mutually invisible, giving natural concurrency isolation; the context instance itself is immutable.
    internal static readonly AsyncLocal<HayateLeaseContext?> Flow = new();

    private static long _leaseIdCounter;

    /// <summary>The process-wide unique lease id (monotonically increasing).</summary>
    public long LeaseId { get; }

    /// <summary>The call-stack frames captured at borrow time (low-overhead capture with fNeedFileInfo:false; may be an empty array).</summary>
    public StackFrame[] Frames { get; }

    /// <summary>The borrow timestamp (Stopwatch ticks, same basis as the pool's duration checks).</summary>
    public long BorrowedAt { get; }

    /// <summary>The lease context for the current async flow; null when not within a lease period (not captured, or already ended).</summary>
    public static HayateLeaseContext? Current => Flow.Value;

    internal HayateLeaseContext(StackFrame[] frames, long borrowedAt)
    {
        LeaseId = Interlocked.Increment(ref _leaseIdCounter);
        Frames = frames ?? Array.Empty<StackFrame>();
        BorrowedAt = borrowedAt;
    }

    /// <summary>Writes this lease into the current async flow (called on the borrow path).</summary>
    internal void AttachToFlow() => Flow.Value = this;

    /// <summary>Ends the lease for the current async flow (called on the Release path; the write has an execution-context copy cost and is gated by the caller).</summary>
    internal static void DetachFromFlow() => Flow.Value = null;
}

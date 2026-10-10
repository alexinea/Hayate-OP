using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Samples.Async;

/// <summary>
/// A stand-in for an expensive async resource (a DB connection, an HTTP client, ...). The pool creates
/// instances with the parameterless constructor; the sample drives the realistic open/query/close lifecycle.
/// </summary>
/// <remarks>
/// The class is intentionally minimal: there is no real network behind it. <see cref="OpenAsync"/>,
/// <see cref="QueryAsync"/> and <see cref="CloseAsync"/> each <see cref="Task.Delay(int, CancellationToken)"/>
/// for 1 ms to stand in for a round-trip, and they all honor a <see cref="CancellationToken"/> so a cancelled
/// lease cancels the in-flight work the same way a real async resource would behave.
/// </remarks>
internal sealed class DbConnection
{
    // A process-wide counter that hands out a stable, increasing Id to every connection the pool builds.
    // The Id is used only to make the sample's console output traceable; it has no effect on pool behavior.
    private static int _counter;

    /// <summary>Monotonic identifier assigned to the connection at construction time (1, 2, 3, ...).</summary>
    /// <remarks>Read-only after construction. Surfaced in <see cref="QueryAsync"/> output to show which object served a call.</remarks>
    public int Id { get; } = Interlocked.Increment(ref _counter);

    /// <summary>Whether <see cref="OpenAsync"/> has completed and <see cref="CloseAsync"/> has not yet run.</summary>
    /// <remarks>Guards <see cref="QueryAsync"/>: querying before opening throws, mirroring a real connection's contract.</remarks>
    public bool IsConnected { get; private set; }

    /// <summary>Simulates establishing the connection (a handshake / TCP + TLS / auth round-trip).</summary>
    /// <param name="ct">Cancellation token; if cancelled mid-handshake the delay is aborted and the connection stays unopened.</param>
    /// <returns>A task that completes when the connection is considered open.</returns>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        await Task.Delay(1, ct); // simulate a connect round-trip
        IsConnected = true;
    }

    /// <summary>Simulates running a query against the open connection and returning a result.</summary>
    /// <param name="sql">The (pretend) SQL text; echoed back in the result so callers can tell queries apart.</param>
    /// <param name="ct">Cancellation token forwarded to the simulated round-trip.</param>
    /// <returns>A formatted string identifying the serving connection and the query text.</returns>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="OpenAsync"/> has set <see cref="IsConnected"/>.</exception>
    public async Task<string> QueryAsync(string sql, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("connection is not open");
        await Task.Delay(1, ct);
        return $"[{Id}] -> {sql}";
    }

    /// <summary>Simulates tearing the connection down so it can be returned to the pool cleanly.</summary>
    /// <param name="ct">Cancellation token forwarded to the simulated round-trip.</param>
    /// <returns>A task that completes when the connection is considered closed.</returns>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        IsConnected = false;
    }
}

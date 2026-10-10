using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Samples.Async;

/// <summary>
/// A stand-in for an expensive async resource (a DB connection, an HTTP client, ...). The pool creates
/// instances with the parameterless constructor; the sample drives the realistic open/query/close lifecycle.
/// </summary>
internal sealed class DbConnection
{
    private static int _counter;
    public int Id { get; } = Interlocked.Increment(ref _counter);
    public bool IsConnected { get; private set; }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await Task.Delay(1, ct); // simulate a connect round-trip
        IsConnected = true;
    }

    public async Task<string> QueryAsync(string sql, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("connection is not open");
        await Task.Delay(1, ct);
        return $"[{Id}] -> {sql}";
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        IsConnected = false;
    }
}

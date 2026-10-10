namespace DotNetCore.HayateOP.Samples.NamedPools;

/// <summary>
/// A pooled object standing in for a database connection. Each instance gets a process-wide unique
/// identifier so the sample can show that borrows come from distinct named pools.
/// </summary>
internal sealed class DbConnection
{
    // A process-wide monotonically increasing counter shared by every DbConnection instance.
    // Interlocked.Increment makes the assignment thread-safe so ids never collide under concurrency.
    private static int _seq;

    /// <summary>A stable, unique identifier for this connection, assigned at construction time.</summary>
    /// <remarks>Backed by an interlocked static counter, so every pooled connection gets a distinct id.</remarks>
    public int Id { get; } = System.Threading.Interlocked.Increment(ref _seq);

    /// <summary>Returns a human-readable label for the connection, e.g. <c>DbConnection#3</c>.</summary>
    /// <returns>A string built from <see cref="Id"/>.</returns>
    public override string ToString() => $"DbConnection#{Id}";
}

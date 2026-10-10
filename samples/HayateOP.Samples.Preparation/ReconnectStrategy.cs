using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Samples.Preparation;

internal sealed class ReconnectStrategy : IHayatePreparationStrategy<DbConnection>
{
    public Task<bool> IsReadyAsync(DbConnection item, CancellationToken cancellationToken = default)
        => Task.FromResult(item.Connected);

    public Task PrepareAsync(DbConnection item, CancellationToken cancellationToken = default)
    {
        // Real code would re-establish the network session here.
        Console.WriteLine($"   [strategy] reconnecting {item}");
        item.Reconnect();
        return Task.CompletedTask;
    }
}

using System.Threading;
namespace DotNetCore.HayateOP.Samples.Preparation;

internal sealed class DbConnection
{
    private static int _seq;
    public int Id { get; } = Interlocked.Increment(ref _seq);
    public bool Connected { get; private set; } = true;
    public void Drop() => Connected = false;
    public void Reconnect() { Connected = true; }
    public void Dispose() { }
    public override string ToString() => $"DbConnection#{Id} (Connected={Connected})";
}

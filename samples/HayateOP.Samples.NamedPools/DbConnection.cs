namespace DotNetCore.HayateOP.Samples.NamedPools;

internal sealed class DbConnection
{
    private static int _seq;
    public int Id { get; } = System.Threading.Interlocked.Increment(ref _seq);
    public override string ToString() => $"DbConnection#{Id}";
}

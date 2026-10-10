namespace DotNetCore.HayateOP.Samples.Lease;

internal sealed class Worker
{
    public int Id { get; set; }
    public void DoWork(string tag) => Console.WriteLine($"   Worker#{Id} working on {tag}");
}

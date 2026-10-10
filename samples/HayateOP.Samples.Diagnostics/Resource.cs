using System.Threading;

namespace DotNetCore.HayateOP.Samples.Diagnostics;

internal sealed class Resource
{
    private static int _counter;
    public int Id { get; } = Interlocked.Increment(ref _counter);

    public void DoWork(string tag) => Console.WriteLine($"   Resource #{Id} working on: {tag}");
}

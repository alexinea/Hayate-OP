namespace DotNetCore.HayateOP.Tests;

public class TestPooledObject : IHayateOpResettable, IDisposable
{
    public int Id { get; set; }
    public string? Data { get; set; }
    public bool IsDisposed { get; private set; }

    public void Reset()
    {
        Id = 0;
        Data = null;
    }

    public void Dispose() => IsDisposed = true;
}
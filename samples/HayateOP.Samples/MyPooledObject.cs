namespace DotNetCore.HayateOP.Samples;

public class MyPooledObject : IHayateResettable, IDisposable
{
    public int Id { get; set; }
    public string Data { get; set; } = string.Empty;
    
    public bool IsDisposed { get; private set; }

    public void Reset()
    {
        Id = 0;
        Data = string.Empty;
    }

    public void Dispose()
    {
        // 释放资源
    }
}
using DotNetCore.HayateOP;

namespace HayateOP.Samples;

public class MyPooledObject : IResettable, IDisposable
{
    public int Id { get; set; }
    public string Data { get; set; }
    
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
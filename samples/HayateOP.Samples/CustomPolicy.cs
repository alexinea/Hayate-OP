using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Samples;

public class CustomPolicy : IHayateObjectPolicy<MyPooledObject>
{
    public MyPooledObject Create()
    {
        return new MyPooledObject
        {
            Id = -1,
            Data = "Init"
        };
    }

    public bool OnRelease(MyPooledObject item)
    {
        item.Id = 0;
        item.Data = string.Empty;

        // Return true to return the object to the pool; return false to destroy it immediately.
        return true;
    }

    public bool Validate(MyPooledObject item)
    {
        return true;
    }

    public void OnAcquire(MyPooledObject item)
    {
    }

    public void OnPassivate(MyPooledObject item)
    {
    }

    public void OnDestroy(MyPooledObject item)
    {
    }
}
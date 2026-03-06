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

    public bool Return(MyPooledObject item)
    {
        item.Id = 0;
        item.Data = string.Empty;

        // 通过 true 表示可以放回池，false 表示直接销毁
        return true;
    }

    public bool Validate(MyPooledObject item)
    {
        return true;
    }

    public void ActivateObject(MyPooledObject item)
    {
    }

    public void PassivateObject(MyPooledObject item)
    {
    }

    public void DestroyObject(MyPooledObject item)
    {
    }
}
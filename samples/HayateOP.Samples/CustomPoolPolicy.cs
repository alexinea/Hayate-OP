using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Samples;

public class CustomPoolPolicy: IHayateObjectPolicy<MyPooledObject>
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
}
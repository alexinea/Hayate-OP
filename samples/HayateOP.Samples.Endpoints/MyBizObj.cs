using DotNetCore.HayateOP;

namespace HayateOP.Samples.Endpoints;

public class MyBizObj: IHayateResettable, IHayateValidatable
{
    public Guid Id { get; } = Guid.NewGuid();
    public void Reset() => Console.WriteLine("对象已重置");
    public bool IsValid() => true;
}
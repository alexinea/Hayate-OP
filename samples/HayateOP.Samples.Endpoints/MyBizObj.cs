using DotNetCore.HayateOP;

namespace HayateOP.Samples.Endpoints;

public class MyBizObj: IHayateResettable, IHayateValidatable
{
    public Guid Id { get; } = Guid.NewGuid();
    public void Reset() => Console.WriteLine("Object reset");
    public bool IsValid() => true;
}
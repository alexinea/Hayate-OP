using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Samples.Validation;

internal sealed class Session : IHayateValidatable
{
    public int Id { get; set; }
    public bool Closed { get; private set; }
    public void Use() { }
    public void Close() => Closed = true;
    public bool IsValid() => !Closed;
}

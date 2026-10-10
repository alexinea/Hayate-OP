namespace DotNetCore.HayateOP.Samples.Unbounded;

internal sealed class ReusableBuffer
{
    private System.Text.StringBuilder _sb = new();
    public string Tag { get; set; } = "";
    public void Reset() => _sb.Clear();
    public void Append(string s) => _sb.Append(s);
    public override string ToString() => $"[{Tag}] {_sb}";
}

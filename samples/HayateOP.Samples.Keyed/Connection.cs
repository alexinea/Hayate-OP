namespace DotNetCore.HayateOP.Samples.Keyed;

internal sealed class Connection
{
    public string ConnectionString { get; init; } = string.Empty;
    public bool IsOpen { get; set; }
}

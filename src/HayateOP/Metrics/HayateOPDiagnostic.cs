using System.Diagnostics;

namespace DotNetCore.HayateOP.Metrics;

public static class HayateOPDiagnostic
{
    public static readonly DiagnosticSource Source = new DiagnosticListener("HayateOP");
    public const string ObjectGet = "HayateOP.Object.Get";
    public const string ObjectReturn = "HayateOP.Object.Return";
    // public const string ObjectCreated = "ObjectPool.Object.Created";
    public const string ObjectMiss = "ObjectPool.Object.Miss";
}
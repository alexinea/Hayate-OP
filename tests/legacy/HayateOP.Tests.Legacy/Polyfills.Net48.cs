// net48 smoke test: .NET Framework 4.8 lacks System.Runtime.CompilerServices.IsExternalInit
// (C# 9's init accessor depends on this type; .NET 5+ / net6+ provide it built into the BCL).
// Only takes effect when compiling the net48 target -- net6.0/net7.0 targets use the BCL built-in definition, with no mutual interference.
// Same convention as the library source: higher versions prefer the framework's built-in capability, while lower versions are patched with the minimal equivalent code.
#if NET48
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

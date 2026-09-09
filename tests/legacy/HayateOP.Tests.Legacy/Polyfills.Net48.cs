// S2 net48 冒烟：.NET Framework 4.8 缺少 System.Runtime.CompilerServices.IsExternalInit
// （C# 9 的 init 访问器依赖该类型；.NET 5+ / net6+ 已由 BCL 内置）。
// 仅在 net48 目标编译时生效——net6.0/net7.0 目标使用 BCL 内置定义，互不影响。
// 与库源码保持同一约定：高版本优先使用框架自带能力，低版本以最小等价代码补齐。
#if NET48
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

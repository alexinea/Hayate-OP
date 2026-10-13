; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
HYO001 | DotNetCore.HayateOP | Warning | A pooled object is acquired and never released
HYO003 | DotNetCore.HayateOP | Warning | A disposed pool is used again
HYO004 | DotNetCore.HayateOP | Info | A synchronous acquire is used inside an async method
HYO005 | DotNetCore.HayateOP | Info | An async acquire does not forward the available cancellation token
HYO006 | DotNetCore.HayateOP | Warning | OnRelease disposes the item instead of releasing it
HYO009 | DotNetCore.HayateOP | Error | A lease or scope is acquired and never disposed

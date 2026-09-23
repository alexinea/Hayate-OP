using System;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The 2.x name of the per-pool Microsoft.Extensions.Logging factory. 3.0 moved the type to
/// <c>DotNetCore.HayateOP.DependencyInjection</c>; this compatibility shell is what is left behind so that 2.x source keeps
/// compiling.
/// </summary>
/// <remarks>
/// It derives from the moved type and adds nothing, so it behaves exactly as it did before the move —
/// the obsolete marker is the only visible difference. Take the type from <c>DotNetCore.HayateOP.DependencyInjection</c> in new
/// code: that is the namespace of the package that actually references Microsoft.Extensions.Logging,
/// and the one the container resolves.
/// A file that imports both namespaces now sees two types of this name and has to qualify one of them
/// (CS0104). Dropping the <c>using</c> for this namespace is the fix, not qualifying the call site.
/// </remarks>
[Obsolete("Use DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerFactory instead. The type moved to that namespace in 3.0 and this compatibility shell will be removed in a future major version.")]
public class HayateMicrosoftLoggerFactory : global::DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerFactory
{
    /// <summary>
    /// Creates a factory over a Microsoft.Extensions.Logging logger factory.
    /// </summary>
    /// <param name="loggerFactory">
    /// The MEL logger factory to create per-category loggers from, or <c>null</c> when the host has no
    /// logging provider — every logger created is then the built-in one.
    /// </param>
    public HayateMicrosoftLoggerFactory(ILoggerFactory? loggerFactory)
        : base(loggerFactory)
    {
    }
}

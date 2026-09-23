using System;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The 2.x name of the Microsoft.Extensions.Logging bridge. 3.0 moved the type to
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
[Obsolete("Use DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerAdapter instead. The type moved to that namespace in 3.0 and this compatibility shell will be removed in a future major version.")]
public class HayateMicrosoftLoggerAdapter : global::DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerAdapter
{
    /// <summary>
    /// Wraps a logger obtained from Microsoft.Extensions.Logging.
    /// </summary>
    /// <param name="logger">The MEL logger, or <c>null</c> to discard everything written to it.</param>
    public HayateMicrosoftLoggerAdapter(ILogger? logger)
        : base(logger)
    {
    }
}

/// <summary>
/// The 2.x name of the generic Microsoft.Extensions.Logging bridge; see
/// <see cref="HayateMicrosoftLoggerAdapter"/> for why it is still here.
/// </summary>
/// <typeparam name="T">The type whose MEL category the logger writes under.</typeparam>
[Obsolete("Use DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerAdapter<T> instead. The type moved to that namespace in 3.0 and this compatibility shell will be removed in a future major version.")]
public class HayateMicrosoftLoggerAdapter<T> : global::DotNetCore.HayateOP.DependencyInjection.HayateMicrosoftLoggerAdapter<T>
{
    /// <summary>
    /// Wraps the logger for the category of <typeparamref name="T"/>.
    /// </summary>
    /// <param name="logger">The MEL logger, or <c>null</c> to discard everything written to it.</param>
    public HayateMicrosoftLoggerAdapter(ILogger<T>? logger)
        : base(logger)
    {
    }
}

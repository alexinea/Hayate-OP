#if NET6_0

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// Provides a MapGroup compatibility implementation for .NET 6.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// MapGroup method compatible with .NET 7; the usage is identical.
    /// </summary>
    /// <param name="builder">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix.</param>
    /// <returns>The route group builder.</returns>
    public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder builder, string prefix)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (prefix == null)
            throw new ArgumentNullException(nameof(prefix));

        // Manually create the RouteGroupBuilder on .NET 6 (the underlying logic matches .NET 7).
        return new RouteGroupBuilder(builder, prefix);
    }
}

#endif
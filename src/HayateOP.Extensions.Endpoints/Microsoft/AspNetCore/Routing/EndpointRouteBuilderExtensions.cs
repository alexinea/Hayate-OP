#if NET6_0

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// 为 .NET 6 提供 MapGroup 兼容实现
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// 兼容 .NET 7 的 MapGroup 方法，用法完全一致
    /// </summary>
    /// <param name="builder">Endpoint 构建器</param>
    /// <param name="prefix">路由前缀</param>
    /// <returns>路由组构建器</returns>
    public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder builder, string prefix)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (prefix == null)
            throw new ArgumentNullException(nameof(prefix));

        // .NET 6 中手动创建 RouteGroupBuilder（底层逻辑和 .NET 7 一致）
        return new RouteGroupBuilder(builder, prefix);
    }
}

#endif
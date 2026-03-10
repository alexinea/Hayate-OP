#if NET6_0
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// .NET 6 兼容的 RouteGroupBuilder 实现
/// </summary>
public sealed class RouteGroupBuilder : IEndpointConventionBuilder
{
    private readonly IEndpointRouteBuilder _builder;
    private readonly string _prefix;
    private readonly List<Action<EndpointBuilder>> _conventions = new();
    private readonly List<object> _groupMetadata = new();

    public RouteGroupBuilder(IEndpointRouteBuilder builder, string prefix)
    {
        _builder = builder;
        _prefix = prefix.StartsWith("/") ? prefix : $"/{prefix}";
    }

    public RouteHandlerBuilder MapGet(string pattern, Delegate handler)
        => BuildAndApplyMetadata(_builder.MapGet(CombinePattern(_prefix, pattern), handler));

    public RouteHandlerBuilder MapPost(string pattern, Delegate handler)
        => BuildAndApplyMetadata(_builder.MapPost(CombinePattern(_prefix, pattern), handler));

    public RouteHandlerBuilder MapPut(string pattern, Delegate handler)
        => BuildAndApplyMetadata(_builder.MapPut(CombinePattern(_prefix, pattern), handler));

    public RouteHandlerBuilder MapDelete(string pattern, Delegate handler)
        => BuildAndApplyMetadata(_builder.MapDelete(CombinePattern(_prefix, pattern), handler));

    public RouteHandlerBuilder MapMethods(string pattern, IEnumerable<string> httpMethods, Delegate handler)
        => BuildAndApplyMetadata(_builder.MapMethods(CombinePattern(_prefix, pattern), httpMethods, handler));

    /// <summary>
    /// 为组内所有 Endpoint 添加 Tags（用于 Swagger 文档）
    /// </summary>
    public RouteGroupBuilder WithTags(params string[] tags)
    {
        _groupMetadata.Add(new TagsAttribute(tags));
        return this;
    }

    /// <summary>
    /// 为组内所有 Endpoint 设置 GroupName（用于 Swagger 文档分组）
    /// </summary>
    public RouteGroupBuilder WithGroupName(string groupName)
    {
        _groupMetadata.Add(new EndpointGroupNameAttribute(groupName));
        return this;
    }

    /// <summary>
    /// 为组内所有 Endpoint 启用授权
    /// </summary>
    public RouteGroupBuilder RequireAuthorization()
    {
        _groupMetadata.Add(new AuthorizeAttribute());
        return this;
    }

    /// <summary>
    /// 为组内所有 Endpoint 启用授权（指定策略）
    /// </summary>
    public RouteGroupBuilder RequireAuthorization(string policyName)
    {
        _groupMetadata.Add(new AuthorizeAttribute(policyName));
        return this;
    }

    /// <summary>
    /// 为组内所有 Endpoint 添加自定义 Metadata（通用扩展）
    /// </summary>
    public RouteGroupBuilder WithMetadata(params object[] metadata)
    {
        _groupMetadata.AddRange(metadata);
        return this;
    }

    #region 实现 IEndpointConventionBuilder 接口

    /// <summary>
    /// 添加组级别的约定（自动应用到所有子 Endpoint）
    /// </summary>
    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention)
    {
        if (convention == null) throw new ArgumentNullException(nameof(convention));
        _conventions.Add(convention);
    }

    #endregion

    // 将组 Metadata 应用到每个子 Endpoint
    private RouteHandlerBuilder BuildAndApplyMetadata(RouteHandlerBuilder handlerBuilder)
    {
        // 1. 应用组级别的 Metadata
        foreach (var metadata in _groupMetadata)
        {
            handlerBuilder.WithMetadata(metadata);
        }

        // 2. 应用组级别的约定（通过 IEndpointConventionBuilder.Add 添加的）
        foreach (var convention in _conventions)
        {
            handlerBuilder.Add(convention);
        }

        return handlerBuilder;
    }

    // 拼接路由前缀和子路由
    private string CombinePattern(string prefix, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return prefix;

        pattern = pattern.StartsWith("/") ? pattern.TrimStart('/') : pattern;
        return $"{prefix.TrimEnd('/')}/{pattern}";
    }
}

#endif
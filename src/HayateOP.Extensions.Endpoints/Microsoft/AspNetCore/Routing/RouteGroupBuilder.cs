#if NET6_0
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// RouteGroupBuilder implementation compatible with .NET 6.
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
    /// Adds tags to all endpoints in the group (used for Swagger documentation).
    /// </summary>
    public RouteGroupBuilder WithTags(params string[] tags)
    {
        _groupMetadata.Add(new TagsAttribute(tags));
        return this;
    }

    /// <summary>
    /// Sets the group name for all endpoints in the group (used for Swagger documentation grouping).
    /// </summary>
    public RouteGroupBuilder WithGroupName(string groupName)
    {
        _groupMetadata.Add(new EndpointGroupNameAttribute(groupName));
        return this;
    }

    /// <summary>
    /// Enables authorization for all endpoints in the group.
    /// </summary>
    public RouteGroupBuilder RequireAuthorization()
    {
        _groupMetadata.Add(new AuthorizeAttribute());
        return this;
    }

    /// <summary>
    /// Enables authorization for all endpoints in the group with the specified policy.
    /// </summary>
    public RouteGroupBuilder RequireAuthorization(string policyName)
    {
        _groupMetadata.Add(new AuthorizeAttribute(policyName));
        return this;
    }

    /// <summary>
    /// Adds custom metadata to all endpoints in the group (general-purpose extension).
    /// </summary>
    public RouteGroupBuilder WithMetadata(params object[] metadata)
    {
        _groupMetadata.AddRange(metadata);
        return this;
    }

    #region Implementation of IEndpointConventionBuilder interface

    /// <summary>
    /// Adds a group-level convention (automatically applied to all child endpoints).
    /// </summary>
    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention)
    {
        if (convention == null) throw new ArgumentNullException(nameof(convention));
        _conventions.Add(convention);
    }

    #endregion

    // Apply the group metadata to each child endpoint.
    private RouteHandlerBuilder BuildAndApplyMetadata(RouteHandlerBuilder handlerBuilder)
    {
        // 1. Apply the group-level metadata.
        foreach (var metadata in _groupMetadata)
        {
            handlerBuilder.WithMetadata(metadata);
        }

        // 2. Apply the group-level conventions (added via IEndpointConventionBuilder.Add).
        foreach (var convention in _conventions)
        {
            handlerBuilder.Add(convention);
        }

        return handlerBuilder;
    }

    // Concatenate the route prefix and the child route.
    private string CombinePattern(string prefix, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return prefix;

        pattern = pattern.StartsWith("/") ? pattern.TrimStart('/') : pattern;
        return $"{prefix.TrimEnd('/')}/{pattern}";
    }
}

#endif

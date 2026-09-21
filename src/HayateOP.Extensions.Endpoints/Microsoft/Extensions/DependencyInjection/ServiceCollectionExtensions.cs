using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
#if NET6_0
using Swashbuckle.AspNetCore.Annotations;
#endif

namespace Microsoft.Extensions.DependencyInjection;

public static class EndpointsExtensions
{
    /// <summary>
    /// Maps the HayateOP management endpoints (overview, pool list, pool detail, configuration
    /// update, statistics, and clear) under the "/hayateop" route group.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint convention builder for the mapped group.</returns>
    /// <remarks>
    /// OpenAPI operation metadata is emitted through three different surfaces, because no single
    /// one spans every target: <c>net6.0</c> uses Swashbuckle's <c>SwaggerOperationAttribute</c>;
    /// <c>net10.0</c> and later use the framework's own <c>WithSummary</c> / <c>WithDescription</c>
    /// (<c>WithOpenApi</c> is obsolete there, ASPDEPR002); the remaining targets keep
    /// <c>WithOpenApi</c>, which is neither obsolete nor replaced on them. The three produce the
    /// same summary and description in the generated document.
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapHayatePoolEndpoints();   // maps GET /hayateop, /hayateop/pools, ...
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapHayatePoolEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/hayateop")
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Hayate Object Pool Management API",
                description: "Endpoints for managing Hayate Object Pools, including configuration and statistics."))
#elif NET10_0_OR_GREATER
            .WithSummary("Hayate Object Pool Management API")
            .WithDescription("Endpoints for managing Hayate Object Pools, including configuration and statistics.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Hayate Object Pool Management API";
                op.Description = "Endpoints for managing Hayate Object Pools, including configuration and statistics.";
                return op;
            })
#endif
            .WithTags("Hayate Object Pool Management")
            .WithGroupName("HayateOP");

        // Overview.
        group.MapGet("/", GetOverviewAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Get pool overview",
                description: "Returns basic information and the version of the management endpoint."))
#elif NET10_0_OR_GREATER
            .WithSummary("Get pool overview")
            .WithDescription("Returns basic information and the version of the management endpoint.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Get pool overview";
                op.Description = "Returns basic information and the version of the management endpoint.";
                return op;
            })
#endif
            .WithName("GetHayatePoolOverview")
            .Produces<HayatePoolOverview>(StatusCodes.Status200OK);

        // Pool list (enumerated from the full registry; returns an empty list when the registry is
        // not injected, without affecting the Type.GetType fallback addressing).
        group.MapGet("/pools", GetPoolsAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Get pool list",
                description: "Enumerates all pools in the registry (name, element type, registration time, pooled/borrowed counts)."))
#elif NET10_0_OR_GREATER
            .WithSummary("Get pool list")
            .WithDescription("Enumerates all pools in the registry (name, element type, registration time, pooled/borrowed counts).")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Get pool list";
                op.Description = "Enumerates all pools in the registry (name, element type, registration time, pooled/borrowed counts).";
                return op;
            })
#endif
            .WithName("GetHayatePoolList")
            .Produces<IReadOnlyList<HayatePoolSummary>>(StatusCodes.Status200OK);

        // Pool details.
        group.MapGet("/{poolName}", GetPoolDetailAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Get pool details",
                description: "Returns configuration and live statistics for the specified pool."))
#elif NET10_0_OR_GREATER
            .WithSummary("Get pool details")
            .WithDescription("Returns configuration and live statistics for the specified pool.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Get pool details";
                op.Description = "Returns configuration and live statistics for the specified pool.";
                return op;
            })
#endif
            .WithName("GetHayatePoolDetail")
            .Produces<HayatePoolDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Update configuration.
        group.MapPost("/{poolName}", UpdatePoolConfigAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Update pool configuration",
                description: "Validates and updates the pool configuration, returning the operation result."))
#elif NET10_0_OR_GREATER
            .WithSummary("Update pool configuration")
            .WithDescription("Validates and updates the pool configuration, returning the operation result.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Update pool configuration";
                op.Description = "Validates and updates the pool configuration, returning the operation result.";
                return op;
            })
#endif
            .WithName("UpdateHayatePoolConfig")
            .Accepts<HayatePoolOptions>("application/json")
            .Produces<HayatePoolOperationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Statistics.
        group.MapGet("/{poolName}/stats", GetPoolStatsAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Get pool statistics",
                description: "Returns live statistics and the current snapshot of the pool."))
#elif NET10_0_OR_GREATER
            .WithSummary("Get pool statistics")
            .WithDescription("Returns live statistics and the current snapshot of the pool.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Get pool statistics";
                op.Description = "Returns live statistics and the current snapshot of the pool.";
                return op;
            })
#endif
            .WithName("GetHayatePoolStats")
            .Produces<HayatePoolStats>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Clear pool.
        group.MapPost("/{poolName}/clear", ClearPoolAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Clear the specified pool",
                description: "Clears the pool immediately and returns the operation timestamp."))
#elif NET10_0_OR_GREATER
            .WithSummary("Clear the specified pool")
            .WithDescription("Clears the pool immediately and returns the operation timestamp.")
#else
            .WithOpenApi(op =>
            {
                op.Summary = "Clear the specified pool";
                op.Description = "Clears the pool immediately and returns the operation timestamp.";
                return op;
            })
#endif
            .WithName("ClearHayatePool")
            .Produces<HayatePoolOperationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetOverviewAsync(IServiceProvider sp)
    {
        await Task.CompletedTask;
        return Results.Ok(new HayatePoolOverview(
            Message: "Hayate Object Pool Management Endpoint",
            Version: "1.0.0",
            Timestamp: DateTime.UtcNow
        ));
    }

    /// <summary>
    /// Enumerates all pools in the registry. Returns an empty list when the registry is not
    /// injected (e.g. a direct builder scenario), without affecting the Type.GetType fallback
    /// of the single-pool addressing endpoint.
    /// </summary>
    private static async Task<IResult> GetPoolsAsync(IServiceProvider sp)
    {
        await Task.CompletedTask;
        var registry = sp.GetService<IHayateObjectPoolRegistry>();
        if (registry is null || registry.Count == 0)
            return Results.Ok(Array.Empty<HayatePoolSummary>());

        var summaries = new List<HayatePoolSummary>(registry.Count);
        foreach (var entry in registry.GetAll())
        {
            int pooled = 0, borrowed = 0;
            try
            {
                var snapshot = entry.Pool.TakeSnapshot();
                pooled = snapshot.PooledCount;
                borrowed = snapshot.BorrowedCount;
            }
            catch
            {
                // The pool may have been disposed or hit a transient failure: return a 0 count for
                // it without affecting the rest of the enumeration.
            }

            summaries.Add(new HayatePoolSummary(
                entry.PoolName,
                entry.ElementType?.FullName,
                entry.RegisteredAt,
                pooled,
                borrowed));
        }

        return Results.Ok(summaries);
    }

    private static async Task<IResult> GetPoolDetailAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        // Resolve the pool from the registry by logical pool name, replacing Type.GetType reflection
        // (which cannot resolve a plain type name).
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"Pool '{poolName}' does not exist", statusCode: StatusCodes.Status404NotFound);

        var stats = pool.GetStats();
        return Results.Ok(new HayatePoolDetail(poolName, pool.GetOptions(), stats));
    }

    private static async Task<IResult> UpdatePoolConfigAsync(string poolName, HayatePoolOptions newConfig, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!newConfig.IsValid())
            return Results.Problem("Invalid configuration", statusCode: StatusCodes.Status400BadRequest);

        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"Pool '{poolName}' does not exist", statusCode: StatusCodes.Status404NotFound);

        pool.ReloadConfig(opt =>
        {
            opt.MinPoolSize = newConfig.MinPoolSize;
            opt.MaxPoolSize = newConfig.MaxPoolSize;
            // Other configuration items...
        });

        return Results.Ok(new HayatePoolOperationResult(poolName, "Configuration updated successfully", DateTime.UtcNow));
    }

    private static async Task<IResult> GetPoolStatsAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"Pool '{poolName}' does not exist", statusCode: StatusCodes.Status404NotFound);

        var stats = pool.GetStats();
        var snapshot = pool.TakeSnapshot();
        return Results.Ok(new HayatePoolStatsDetail(poolName, stats, snapshot));
    }

    private static async Task<IResult> ClearPoolAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"Pool '{poolName}' does not exist", statusCode: StatusCodes.Status404NotFound);

        pool.Clear();
        return Results.Ok(new HayatePoolOperationResult(poolName, "Pool cleared successfully", DateTime.UtcNow));
    }

    /// <summary>
    /// Prefers resolving the pool from the registry by logical pool name; if the registry is not
    /// injected (e.g. using the builder directly instead of DI registration), falls back to
    /// resolving the registered IHayateObjectPool&lt;T&gt; by type name.
    /// </summary>
    private static bool TryResolve(IServiceProvider sp, string poolName, out IHayateObjectPool pool)
    {
        var registry = sp.GetService<IHayateObjectPoolRegistry>();
        if (registry != null && registry.TryGet(poolName, out pool!))
            return true;

        // Fallback: when the registry is missing or not yet populated, resolve via DI by type name
        // (compatible with the legacy registration approach).
        var poolType = Type.GetType(poolName);
        if (poolType != null)
        {
            var poolInterface = typeof(IHayateObjectPool<>).MakeGenericType(poolType);
            var resolved = sp.GetService(poolInterface) as IHayateObjectPool;
            if (resolved != null)
            {
                pool = resolved;
                return true;
            }
        }

        pool = null!;
        return false;
    }
}

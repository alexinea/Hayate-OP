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
    public static IEndpointConventionBuilder MapHayatePoolEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/hayateop")
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "Hayate Object Pool Management API",
                description: "Endpoints for managing Hayate Object Pools, including configuration and statistics."))
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

        // 概览
        group.MapGet("/", GetOverviewAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "获取池概览",
                description: "返回管理端点的基本信息和版本号"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "获取池概览";
                op.Description = "返回管理端点的基本信息和版本号";
                return op;
            })
#endif
            .WithName("GetHayatePoolOverview")
            .Produces<HayatePoolOverview>(StatusCodes.Status200OK);

        // M11+：池列表（走注册表完整版枚举；未注入注册表时返回空列表，不影响 Type.GetType 兜底寻址）
        group.MapGet("/pools", GetPoolsAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "获取池列表",
                description: "枚举注册表中的全部池（名称、元素类型、注册时间、池内/借出计数）"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "获取池列表";
                op.Description = "枚举注册表中的全部池（名称、元素类型、注册时间、池内/借出计数）";
                return op;
            })
#endif
            .WithName("GetHayatePoolList")
            .Produces<IReadOnlyList<HayatePoolSummary>>(StatusCodes.Status200OK);

        // 池详情
        group.MapGet("/{poolName}", GetPoolDetailAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "获取指定池详情",
                description: "根据池名称返回配置信息和实时统计"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "获取指定池详情";
                op.Description = "根据池名称返回配置信息和实时统计";
                return op;
            })
#endif
            .WithName("GetHayatePoolDetail")
            .Produces<HayatePoolDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 更新配置
        group.MapPost("/{poolName}", UpdatePoolConfigAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "更新池配置",
                description: "验证并更新池配置，返回操作结果"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "更新池配置";
                op.Description = "验证并更新池配置，返回操作结果";
                return op;
            })
#endif
            .WithName("UpdateHayatePoolConfig")
            .Accepts<HayatePoolOptions>("application/json")
            .Produces<HayatePoolOperationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // 统计数据
        group.MapGet("/{poolName}/stats", GetPoolStatsAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "获取池统计数据",
                description: "返回池的实时统计和当前快照"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "获取池统计数据";
                op.Description = "返回池的实时统计和当前快照";
                return op;
            })
#endif
            .WithName("GetHayatePoolStats")
            .Produces<HayatePoolStats>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 清空池
        group.MapPost("/{poolName}/clear", ClearPoolAsync)
#if NET6_0
            .WithMetadata(new SwaggerOperationAttribute(
                summary: "清空指定池",
                description: "立即清空池，返回操作时间戳"))
#else
            .WithOpenApi(op =>
            {
                op.Summary = "清空指定池";
                op.Description = "立即清空池，返回操作时间戳";
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
    /// M11+：枚举注册表中的全部池。未注入注册表（直接 Builder 场景）时返回空列表，
    /// 不影响单池寻址端点的 Type.GetType 兜底。
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
                // 池可能已被 Dispose / 瞬时故障：该池返回 0 计数，不影响其余池枚举。
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
        // T05：从注册表按「逻辑池名」寻址，替代 Type.GetType 反射（后者无法解析纯类型名）
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var stats = pool.GetStats();
        return Results.Ok(new HayatePoolDetail(poolName, pool.GetOptions(), stats));
    }

    private static async Task<IResult> UpdatePoolConfigAsync(string poolName, HayatePoolOptions newConfig, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!newConfig.IsValid())
            return Results.Problem("无效的配置", statusCode: StatusCodes.Status400BadRequest);

        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        pool.ReloadConfig(opt =>
        {
            opt.MinPoolSize = newConfig.MinPoolSize;
            opt.MaxPoolSize = newConfig.MaxPoolSize;
            // 其他配置项...
        });

        return Results.Ok(new HayatePoolOperationResult(poolName, "配置更新成功", DateTime.UtcNow));
    }

    private static async Task<IResult> GetPoolStatsAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var stats = pool.GetStats();
        var snapshot = pool.TakeSnapshot();
        return Results.Ok(new HayatePoolStatsDetail(poolName, stats, snapshot));
    }

    private static async Task<IResult> ClearPoolAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!TryResolve(sp, poolName, out var pool))
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        pool.Clear();
        return Results.Ok(new HayatePoolOperationResult(poolName, "池清空成功", DateTime.UtcNow));
    }

    /// <summary>
    /// T05：优先按逻辑池名查注册表；若未注入注册表（例如直接用 Builder 而非 DI 注册），
    /// 退回按类型名解析 DI 中注册的 IHayateObjectPool&lt;T&gt;。
    /// </summary>
    private static bool TryResolve(IServiceProvider sp, string poolName, out IHayateObjectPool pool)
    {
        var registry = sp.GetService<IHayateObjectPoolRegistry>();
        if (registry != null && registry.TryGet(poolName, out pool!))
            return true;

        // 兜底：注册表缺失或尚未填充时，按类型名做 DI 解析（兼容历史注册方式）
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
using DotNetCore.HayateOP;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.Extensions.DependencyInjection;

public static class EndpointsExtensions
{
    public static IEndpointConventionBuilder MapHayateOpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/hayateop")
            .WithTags("Hayate Object Pool Management")
            .WithGroupName("HayateOP");

        // 概览
        group.MapGet("/", GetOverviewAsync)
            .WithName("GetHayatePoolOverview")
            .WithSummary("获取池概览")
            .WithDescription("返回管理端点的基本信息和版本号")
            .Produces<HayatePoolOverview>(StatusCodes.Status200OK);

        // 池详情
        group.MapGet("/{poolName}", GetPoolDetailAsync)
            .WithName("GetHayatePoolDetail")
            .WithSummary("获取指定池详情")
            .WithDescription("根据池名称返回配置信息和实时统计")
            .Produces<HayatePoolDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 更新配置
        group.MapPost("/{poolName}", UpdatePoolConfigAsync)
            .WithName("UpdateHayatePoolConfig")
            .WithSummary("更新池配置")
            .WithDescription("验证并更新池配置，返回操作结果")
            .Accepts<HayatePoolOptions>("application/json")
            .Produces<HayatePoolOperationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // 统计数据
        group.MapGet("/{poolName}/stats", GetPoolStatsAsync)
            .WithName("GetHayatePoolStats")
            .WithSummary("获取池统计数据")
            .WithDescription("返回池的实时统计和当前快照")
            .Produces<HayatePoolStats>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 清空池
        group.MapPost("/{poolName}/clear", ClearPoolAsync)
            .WithName("ClearHayatePool")
            .WithSummary("清空指定池")
            .WithDescription("立即清空池，返回操作时间戳")
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

    private static async Task<IResult> GetPoolDetailAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        var poolType = Type.GetType(poolName);
        if (poolType == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var poolInterface = typeof(IHayateObjectPool<>).MakeGenericType(poolType);
        var pool = sp.GetService(poolInterface);
        if (pool == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var stats = (HayatePoolStats)poolInterface.GetMethod("GetStats").Invoke(pool, null);
        return Results.Ok(new HayatePoolDetail(poolName, new HayatePoolOptions(), stats));
    }

    private static async Task<IResult> UpdatePoolConfigAsync(string poolName, HayatePoolOptions newConfig, IServiceProvider sp)
    {
        await Task.CompletedTask;
        if (!newConfig.IsValid())
            return Results.Problem("无效的配置", statusCode: StatusCodes.Status400BadRequest);

        var poolType = Type.GetType(poolName);
        if (poolType == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var poolInterface = typeof(IHayateObjectPool<>).MakeGenericType(poolType);
        var pool = sp.GetService(poolInterface);
        if (pool == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        poolInterface.GetMethod("ReloadConfig").Invoke(pool, new object[] { new Action<HayatePoolOptions>(opt =>
            {
                opt.MinPoolSize = newConfig.MinPoolSize;
                opt.MaxPoolSize = newConfig.MaxPoolSize;
                // 其他配置项...
            }) });

        return Results.Ok(new HayatePoolOperationResult(poolName, "配置更新成功", DateTime.UtcNow));
    }

    private static async Task<IResult> GetPoolStatsAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        var poolType = Type.GetType(poolName);
        if (poolType == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var poolInterface = typeof(IHayateObjectPool<>).MakeGenericType(poolType);
        var pool = sp.GetService(poolInterface);
        if (pool == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var stats = (HayatePoolStats)poolInterface.GetMethod("GetStats").Invoke(pool, null);
        var snapshot = (HayatePoolSnapshot)poolInterface.GetMethod("TakeSnapshot").Invoke(pool, null);
        return Results.Ok(new HayatePoolStatsDetail(poolName, stats, snapshot));
    }

    private static async Task<IResult> ClearPoolAsync(string poolName, IServiceProvider sp)
    {
        await Task.CompletedTask;
        var poolType = Type.GetType(poolName);
        if (poolType == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        var poolInterface = typeof(IHayateObjectPool<>).MakeGenericType(poolType);
        var pool = sp.GetService(poolInterface);
        if (pool == null)
            return Results.Problem($"池 {poolName} 不存在", statusCode: StatusCodes.Status404NotFound);

        poolInterface.GetMethod("Clear").Invoke(pool, null);
        return Results.Ok(new HayatePoolOperationResult(poolName, "池清空成功", DateTime.UtcNow));
    }
}
using DotNetCore.HayateOP;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IEndpointConventionBuilder MapHayateOpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 1. 配置路由组：集中管理授权、标签、OpenAPI 分组
        var group = endpoints.MapGroup("/hayateop")
            .WithTags("Hayate Object Pool Management")
            .WithGroupName("HayateOp");

        group.MapGet("/", GetAllHayatePoolsAsync)
            .WithName("GetAllHayatePools")
            .WithSummary("获取所有 Hayate 对象池概览")
            .WithDescription("返回管理端点的基本信息和版本号")
            .Produces<HayatePoolOverview>(StatusCodes.Status200OK);

        group.MapGet("/{poolName}", GetHayatePoolConfigAsync)
            .WithName("GetHayatePoolConfig")
            .WithSummary("获取指定池的配置")
            .WithDescription("根据池名称返回配置信息和实时统计")
            .Produces<HayatePoolDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{poolName}", UpdateHayatePoolConfigAsync)
            .WithName("UpdateHayatePoolConfig")
            .WithSummary("更新指定池的配置")
            .WithDescription("验证并更新池配置，返回操作结果")
            .Accepts<HayatePoolOptions>("application/json")
            .Produces<HayatePoolOperationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{poolName}/stats", GetHayatePoolStatsAsync)
            .WithName("GetHayatePoolStats")
            .WithSummary("获取指定池的统计数据")
            .WithDescription("返回池的实时统计和当前快照")
            .Produces<HayatePoolStatsDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{poolName}/clear", ClearHayatePoolAsync)
            .WithName("ClearHayatePool")
            .WithSummary("清空指定池的所有对象")
            .WithDescription("立即清空池，返回操作时间戳")
            .Produces<HayatePoolOperationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 1. 配置路由组：仅保留逻辑分组
        //var group = endpoints.MapGroup("/hayateop");

        //// 获取所有 Hayate 对象池概览
        //group.MapGet("/", GetAllHayatePoolsAsync)
        //    .WithName("GetAllHayatePools");

        //// 获取指定池的配置
        //group.MapGet("/{poolName}", GetHayatePoolConfigAsync)
        //    .WithName("GetHayatePoolConfig");

        //// 更新指定池的配置
        //group.MapPost("/{poolName}", UpdateHayatePoolConfigAsync)
        //    .WithName("UpdateHayatePoolConfig");

        //// 获取指定池的统计数据
        //group.MapGet("/{poolName}/stats", GetHayatePoolStatsAsync)
        //    .WithName("GetHayatePoolStats");

        //// 清空指定池的所有对象
        //group.MapPost("/{poolName}/clear", ClearHayatePoolAsync)
        //    .WithName("ClearHayatePool");

        return group;
    }

    private static async Task<IResult> GetAllHayatePoolsAsync(IHayateObjectPoolFactory factory)
    {
        await Task.CompletedTask; // 模拟异步操作
        return Results.Ok(new HayatePoolOverview(
            message: "Hayate Object Pool Management Endpoint",
            version: "1.0"));
    }

    private static async Task<IResult> GetHayatePoolConfigAsync(
        string poolName,
        IHayateObjectPoolFactory factory)
    {
        if (!await factory.PoolExistsAsync(poolName))
            return Results.Problem(
                detail: $"对象池 '{poolName}' 不存在",
                statusCode: StatusCodes.Status404NotFound,
                title: "未找到对象池");

        var config = await factory.GetPoolOptionsAsync(poolName);
        var stats = await factory.GetPoolStatsAsync(poolName);
        return Results.Ok(new HayatePoolDetail(poolName, config, stats));
    }

    private static async Task<IResult> UpdateHayatePoolConfigAsync(
        string poolName,
        [FromBody] HayatePoolOptions newConfig,
        IHayateObjectPoolFactory factory)
    {
        if (!newConfig.IsValid())
            return Results.Problem(
                detail: "配置参数无效",
                statusCode: StatusCodes.Status400BadRequest,
                title: "无效的配置");

        if (!await factory.PoolExistsAsync(poolName))
            return Results.Problem(
                detail: $"对象池 '{poolName}' 不存在",
                statusCode: StatusCodes.Status404NotFound,
                title: "未找到对象池");

        await factory.UpdatePoolOptionsAsync(poolName, newConfig);
        return Results.Ok(new HayatePoolOperationResponse(
            poolName,
            "Configuration updated successfully",
            DateTime.UtcNow));
    }

    private static async Task<IResult> GetHayatePoolStatsAsync(
        string poolName,
        IHayateObjectPoolFactory factory)
    {
        if (!await factory.PoolExistsAsync(poolName))
            return Results.Problem(
                detail: $"对象池 '{poolName}' 不存在",
                statusCode: StatusCodes.Status404NotFound,
                title: "未找到对象池");

        var stats = await factory.GetPoolStatsAsync(poolName);
        var snapshot = await factory.GetPoolSnapshotAsync(poolName);
        return Results.Ok(new HayatePoolStatsDetail(poolName, stats, snapshot));
    }

    private static async Task<IResult> ClearHayatePoolAsync(
        string poolName,
        IHayateObjectPoolFactory factory)
    {
        if (!await factory.PoolExistsAsync(poolName))
            return Results.Problem(
                detail: $"对象池 '{poolName}' 不存在",
                statusCode: StatusCodes.Status404NotFound,
                title: "未找到对象池");

        await factory.ClearPoolAsync(poolName);
        return Results.Ok(new HayatePoolOperationResponse(
            poolName,
            "Pool cleared successfully",
            DateTime.UtcNow));
    }
}
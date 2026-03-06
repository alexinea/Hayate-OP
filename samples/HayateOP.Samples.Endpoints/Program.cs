using DotNetCore.HayateOP;
using HayateOP.Samples.Endpoints;
using System.Text;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

builder.Services.AddHayateObjectPool<MyBizObj>(opt =>
{
    opt.MinPoolSize = 10;
    opt.MaxPoolSize = 100;
    opt.UseFairSemaphore = true;
    opt.EnableMetrics = true;
    opt.ShardCount = 4;
    //opt.ScalingIntervalMs = 1000 * 60 * 60;
    //opt.DefaultGetTimeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddLogging(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Debug);
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHayateOpEndpoints();

app.MapGet("/test-hayateop", async (IHayateObjectPoolFactory factory, ILogger<HayateObjectPool<MyBizObj>> logger) =>
{
    MyBizObj? obj = null;
    IHayateObjectPool<MyBizObj>? pool = null;

    var options = app.Services.GetRequiredService<IOptions<HayatePoolOptions>>();


    try
    {
        pool = factory.GetPool<MyBizObj>(policy: null, options.Value, logger, null);
        logger.LogInformation("获取对象池，当前统计：{Stats}", pool.GetStats());

        // 显式指定获取对象的超时时间（便于排查）
        obj = pool.Get(TimeSpan.FromSeconds(5)); // 覆盖默认BlockTimeout，临时排查
        //obj = pool.Get(); // 覆盖默认BlockTimeout，临时排查
        if (obj == null)
        {
            logger.LogError("从对象池获取MyBizObj失败，对象为null");
            return Results.BadRequest("获取对象池对象失败：对象为null");
        }

        logger.LogInformation("成功获取对象，ID：{ObjId}", obj.Id);
        return Results.Ok(new
        {
            ObjectId = obj.Id,
            PoolStats = pool.GetStats(),
            PoolConfig = pool.GetOptions()
        });
    }
    catch (TimeoutException ex)
    {
        logger.LogError(ex, "获取对象池对象超时：{Message}", ex.Message);
        var timeoutMessage = $"获取对象超时：{ex.Message}，当前池统计：{pool?.GetStats()}";

        return Results.Text(
            content: timeoutMessage,
            contentType: "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "获取/使用对象池对象异常：{Message}", ex.Message);
        var errorMessage = ex.Message;
        return Results.Text(
            content: errorMessage,
            contentType: "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError
        );
    }
    finally
    {
        // 确保对象非null时才归还，避免Return(null)导致的异常
        if (pool != null && obj != null)
        {
            try
            {
                pool.Return(obj);
                logger.LogInformation("成功归还对象，ID：{ObjId}，归还后池统计：{Stats}",
                    obj.Id, pool.GetStats());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "归还对象失败，ID：{ObjId}", obj.Id);
            }
        }
    }
});

app.Run();

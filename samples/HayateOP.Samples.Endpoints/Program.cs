using DotNetCore.HayateOP;
using HayateOP.Samples.Endpoints;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------------------
// HayateOP registration
//   * AddHayatePoolSupport         -> core services (scaling strategy, metrics)
//   * RegisterGlobalConfig         -> bind "HayatePool:Global" from config
//   * RegisterHayatePool<T>(config)-> bind "HayatePool:Pools:MyBizObj" + DI注册
//   * RegisterHealthChecks<T>      -> ASP.NET Core health check integration
//   * RegisterDiagnostics<T>       -> System.Diagnostics metrics bridge
// ---------------------------------------------------------------------------
builder.Services.AddHayatePoolSupport()
    .RegisterGlobalConfig(builder.Configuration)
    .RegisterHayatePool<MyBizObj>(builder.Configuration)
    .RegisterHealthChecks<MyBizObj>()
    .RegisterDiagnostics<MyBizObj>();

builder.Services.AddLogging(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Debug);
});

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

// Built-in HayateOP management endpoints (see README "Management Endpoints").
//   * GET  /hayateop                       -> overview (works)
//   * GET  /hayateop/{type}                -> per-pool detail
//   * GET  /hayateop/{type}/stats          -> per-pool stats
//   * POST /hayateop/{type}                -> update config
//   * POST /hayateop/{type}/clear          -> clear pool
// NOTE: the per-pool endpoints currently resolve {type} via reflection
// (Type.GetType) and have a known limitation (tracked as T05). For reliable,
// programmatic per-pool access prefer the DI-resolved IHayateObjectPool<T>
// shown in the /test-hayateop endpoint below.
app.MapHayatePoolEndpoints();

// Standard ASP.NET Core health endpoint backed by HayateOpHealthCheck<T>.
app.MapHealthChecks("/health");

app.MapGet("/test-hayateop", async (IHayateObjectPool<MyBizObj> pool, ILogger<MyBizObj> logger) =>
{
    MyBizObj? obj = null;

    try
    {
        logger.LogInformation("Acquiring object, current stats: {Stats}", pool.GetStats());

        // Optionally override the default block/timeout behaviour per call.
        obj = pool.Acquire(TimeSpan.FromSeconds(5));
        if (obj == null)
        {
            logger.LogError("Acquiring MyBizObj failed: returned null");
            return Results.BadRequest("Failed to acquire object: null");
        }

        logger.LogInformation("Acquired object, Id: {ObjId}", obj.Id);
        return Results.Ok(new
        {
            ObjectId = obj.Id,
            PoolStats = pool.GetStats(),
            PoolConfig = pool.GetOptions()
        });
    }
    catch (TimeoutException ex)
    {
        logger.LogError(ex, "Timed out acquiring object: {Message}", ex.Message);
        return Results.Text(
            content: $"Acquire timeout: {ex.Message}, stats: {pool?.GetStats()}",
            contentType: "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error acquiring/using object: {Message}", ex.Message);
        return Results.Text(
            content: ex.Message,
            contentType: "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (pool != null && obj != null)
        {
            try
            {
                pool.Release(obj);
                logger.LogInformation("Released object, Id: {ObjId}, stats: {Stats}", obj.Id, pool.GetStats());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to release object, Id: {ObjId}", obj.Id);
            }
        }
    }
});

app.Run();

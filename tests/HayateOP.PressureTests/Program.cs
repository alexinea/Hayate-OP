// See https://aka.ms/new-console-template for more information

using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Logging;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ILogger = Serilog.ILogger;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

const int TestDurationMinutes = 10;
const int ConcurrentThreads = 50;
const int MinPoolSize = 100;
const int MaxPoolSize = 500;

using var pool = new HayatePoolBuilder<PooledResource>()
    .WithPoolName("StressTestPool")
    .WithMinSize(MinPoolSize)
    .WithMaxSize(MaxPoolSize)
    .WithShardCount(4)
    .WithShardCount(8) // 增加分片数
    .WithFairMode(false) // 关闭公平模式，提高性能
    .WithAcquireTimeout(TimeSpan.FromSeconds(10))
    .WithEnableAutoScaling(true)
    .WithScaleUpStep(20) // 增加扩容步长
    .WithScaleUpCooldownSeconds(1) // 减少扩容冷却
    .WithEnableValidation(false) // 关闭验证，提高性能
    .WithEnableEviction(false) // 关闭驱逐，提高性能
    .WithEnableLeakDetection(false) // 关闭泄漏检测，提高性能
    .WithEnableMetrics(true)
    .WithLogger(new SerilogLoggerAdapter<PooledResource>())
    .Build();

var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TestDurationMinutes));
var totalOperations = 0L;
var totalErrors = 0L;
var process = Process.GetCurrentProcess();

Log.Information("=== Hayate Object Pool 压力测试启动 ===");
Log.Information("测试时长: {Duration}分钟", TestDurationMinutes);
Log.Information("并发线程数: {ThreadCount}", ConcurrentThreads);
Log.Information("池大小: {Min}~{Max}", MinPoolSize, MaxPoolSize);
Log.Information("========================================");

_ = Task.Run(() => MonitorPoolStats(cts.Token));

var threads = new List<Thread>();

for (int i = 0; i < ConcurrentThreads; i++)
{
    var thread = new Thread(() => WorkerLoop(cts.Token))
    {
        IsBackground = true,
        Priority = ThreadPriority.Normal
    };
    threads.Add(thread);
    thread.Start();
}

foreach (var thread in threads) thread.Join();

Log.Information("=== 压力测试结束 ===");
Log.Information("总操作数: {TotalOps:N0}", Interlocked.Read(ref totalOperations));
Log.Information("总错误数: {TotalErrors:N0}", Interlocked.Read(ref totalErrors));
Log.Information("错误率: {ErrorRate:F4}%", (double)totalErrors / totalOperations * 100);
Log.Information("最终池大小: {Size}", pool.GetStats().CurrentSize);
Log.Information("最终内存使用: {Memory:F2}MB", process.WorkingSet64 / 1024.0 / 1024.0);
Log.Information("GC总回收次数: {GCCount}", GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2));
Log.Information("====================");

Log.CloseAndFlush();


void WorkerLoop(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            var resource = pool.Acquire();
            try
            {
                resource.DoWork();
                Thread.Sleep(Random.Shared.Next(1, 3));
            }
            finally
            {
                pool.Release(resource);
            }
            Interlocked.Increment(ref totalOperations);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref totalErrors);
            Log.Error(ex, "操作异常");
        }
    }
}

async Task MonitorPoolStats(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), token);
        var stats = pool.GetStats();
        var memory = process.WorkingSet64 / 1024.0 / 1024.0;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        Log.Information("[监控] 池大小: {CurrentSize} | 空闲对象: {PooledCount} | 总创建: {TotalCreated} | 总Miss: {TotalMissed} | 内存: {Memory:F2}MB | GC: {GC0}/{GC1}/{GC2}",
            stats.CurrentSize, stats.PooledCount, stats.TotalCreated, stats.TotalMissed, memory, gc0, gc1, gc2);
    }
}

public class PooledResource : IHayateResettable, IHayateValidatable
{
    private readonly byte[] _buffer = new byte[1024 * 10];
    public int AccessCount { get; private set; }

    public void DoWork()
    {
        AccessCount++;
        for (int i = 0; i < _buffer.Length; i++)
            _buffer[i] = (byte)(i % 256);
    }

    public void Reset() => AccessCount = 0;
    public bool IsValid() => true;
}

public class SerilogLoggerAdapter<T> : IHayateLogger
{
    private readonly ILogger _logger = Log.ForContext<T>();
    public void LogDebug(string message, params object[] args) => _logger.Debug(message, args);
    public void LogInformation(string message, params object[] args) => _logger.Information(message, args);
    public void LogWarning(string message, params object[] args) => _logger.Warning(message, args);
    public void LogError(Exception ex, string message, params object[] args) => _logger.Error(ex, message, args);
}

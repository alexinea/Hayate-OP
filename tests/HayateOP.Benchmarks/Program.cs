// See https://aka.ms/new-console-template for more information

using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Running;
using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;

var summary = BenchmarkRunner.Run<HayateOpBenchmark>();

Console.WriteLine("Hello, World!");


[Config(typeof(BenchmarkConfig))]
public class HayateOpBenchmark
{
    private class PooledObject
    {
        public int Data { get; set; }
        public void Reset() => Data = 0;
    }

    private const int MinPoolSize = 10;
    private const int MaxPoolSize = 100;
    private const int ThreadCount = 16;


    // Hayate池（全功能关闭，极简模式）
    private IHayateObjectPool<PooledObject> _hayateMinimalPool;

    // Hayate池（全功能开启）
    private IHayateObjectPool<PooledObject> _hayateFullFeaturePool;

    // 微软官方池
    private ObjectPool<PooledObject> _microsoftPool;

    public class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(3)            // 预热次数
                .WithIterationCount(10)        // 正式测试次数
                .WithGcServer(true)            // 服务器GC模式
                .WithGcConcurrent(true));      // 并发GC
            
            // 添加诊断器
            AddDiagnoser(MemoryDiagnoser.Default);
            AddDiagnoser(ThreadingDiagnoser.Default);

            // 添加排名列
            AddColumn(RankColumn.Arabic);
            AddColumn(BaselineRatioColumn.RatioMean);
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // 微软池初始化
        var microsoftPolicy = new DefaultPooledObjectPolicy<PooledObject>();
        var provider = new DefaultObjectPoolProvider { MaximumRetained = MaxPoolSize };
        _microsoftPool = provider.Create(microsoftPolicy);


        // Hayate极简池（全功能关闭，和微软池对齐）
        _hayateMinimalPool = new HayatePoolBuilder<PooledObject>()
            .WithMinSize(MinPoolSize)
            .WithMaxSize(MaxPoolSize)
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();

        // Hayate全功能池
        _hayateFullFeaturePool = new HayatePoolBuilder<PooledObject>()
            .WithMinSize(MinPoolSize)
            .WithMaxSize(MaxPoolSize)
            .WithEnableSharding(true)
            .WithShardCount(4)
            .WithEnableAutoScaling(true)
            .WithEnableValidation(true)
            .WithEnableEviction(true)
            .WithEnableGenerationOptimization(true)
            .WithEnableLeakDetection(true)
            .WithEnableMetrics(true)
            .Build();

        // 预热
        PreWarmPool(_microsoftPool, _hayateMinimalPool, _hayateFullFeaturePool);
    }

    private void PreWarmPool(ObjectPool<PooledObject> msPool, IHayateObjectPool<PooledObject> hayateMinPool, IHayateObjectPool<PooledObject> hayateFullPool)
    {
        var msObjs = new List<PooledObject>();
        for (int i = 0; i < MinPoolSize; i++) msObjs.Add(msPool.Get());
        foreach (var obj in msObjs) msPool.Return(obj);

        var hayateMinObjs = new List<PooledObject>();
        for (int i = 0; i < MinPoolSize; i++) hayateMinObjs.Add(hayateMinPool.Acquire());
        foreach (var obj in hayateMinObjs) hayateMinPool.Release(obj);

        var hayateFullObjs = new List<PooledObject>();
        for (int i = 0; i < MinPoolSize; i++) hayateFullObjs.Add(hayateFullPool.Acquire());
        foreach (var obj in hayateFullObjs) hayateFullPool.Release(obj);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hayateMinimalPool.Dispose();
        _hayateFullFeaturePool.Dispose();
    }

    #region 单线程基准测试
    [Benchmark(Baseline = true, Description = "微软官方池 - 单线程")]
    public void MicrosoftPool_SingleThread()
    {
        var obj = _microsoftPool.Get();
        obj.Data++;
        _microsoftPool.Return(obj);
    }

    [Benchmark(Description = "Hayate池 - 极简模式（单线程）")]
    public void HayatePool_Minimal_SingleThread()
    {
        var obj = _hayateMinimalPool.Acquire();
        obj.Data++;
        _hayateMinimalPool.Release(obj);
    }

    [Benchmark(Description = "Hayate池 - 全功能模式（单线程）")]
    public void HayatePool_FullFeature_SingleThread()
    {
        var obj = _hayateFullFeaturePool.Acquire();
        obj.Data++;
        _hayateFullFeaturePool.Release(obj);
    }
    #endregion

    #region 多线程基准测试
    [Benchmark(Description = "微软官方池 - 16线程并发")]
    public void MicrosoftPool_MultiThread()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _microsoftPool.Get();
            obj.Data++;
            _microsoftPool.Return(obj);
        });
    }

    [Benchmark(Description = "Hayate池 - 极简模式（16线程）")]
    public void HayatePool_Minimal_MultiThread()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _hayateMinimalPool.Acquire();
            obj.Data++;
            _hayateMinimalPool.Release(obj);
        });
    }

    [Benchmark(Description = "Hayate池 - 全功能模式（16线程）")]
    public void HayatePool_FullFeature_MultiThread()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _hayateFullFeaturePool.Acquire();
            obj.Data++;
            _hayateFullFeaturePool.Release(obj);
        });
    }
    #endregion

}
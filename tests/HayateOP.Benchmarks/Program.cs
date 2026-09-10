// M1 — HayateOP 标准 BenchmarkDotNet 编排（2.5 / P3）
//
// 覆盖矩阵（Acquire / Release / AcquireAsync 全路径）：
//   · 被测实现：HayateOP Lean（全功能关闭）/ Sharded4（仅分片）/ Full（全功能）
//   · 对照基线：Microsoft.Extensions.ObjectPool（MEOP，Baseline）+ 原生 `new`（无池，分配口径下界）
//   · 异步维度：MEOP / 原生无异步 API，标注 N/A（不在编排内出现）
//   · 并发维度：100 线程 Parallel.For 借还（吞吐 + 争用）
//
// 统计口径：BDN 内置统计列（Mean/Median/StdDev/Min/Max）+ 自定义 P50/P90/P95/P99 分位列
//   （由各迭代平均耗时计算，规避 BDN 无 P99 内置列的缺口）；MemoryDiagnoser 提供 B/Op 分配口径。
// 运行：dotnet run -c Release -- --filter '*'   （不带 filter 时运行本类全部基准）
// 报告：BenchmarkDotNet.Artifacts/results/*-report-github.md（MarkdownExporter）

using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;

BenchmarkRunner.Run<HayateOpBenchmarks>(args: args);

/// <summary>
/// M1：HayateOP 头对头基准编排。
/// </summary>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class HayateOpBenchmarks
{
    /// <summary>被池化对象（与既有 T11 基准同构，保证历史数据可比较）。</summary>
    public class PooledObject
    {
        public int Data { get; set; }
        public void Reset() => Data = 0;
    }

    // 容量配比说明（防 GlobalSetup 卡死）：Hayate 极简/分片池 autoScaling=false，
    // 池空不自动创建。Min=250 保证并发套（100 线程借还）仍有 ≥100 空闲对象可用，
    // 绝不触发 Block 等待（对齐 T11 既有口径，保证与历史基线可比）。
    private const int MinPoolSize = 250;
    private const int MaxPoolSize = 300;
    private const int ThreadCount = 100;
    private const int ReleaseSlots = 32;      // Release 套的借出槽位（2 的幂，取模免分支）

    private class BenchConfig : ManualConfig
    {
        public BenchConfig()
        {
            // 短迭代 Job：3 预热 + 10 迭代，防长跑挂起
            AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcServer(true)
                .WithGcConcurrent(true)
                .WithId("Short"));

            AddColumn(StatisticColumn.StdDev);
            AddColumn(StatisticColumn.Median);
            AddColumn(new PercentileColumn("P50", 0.50));
            AddColumn(new PercentileColumn("P90", 0.90));
            AddColumn(new PercentileColumn("P95", 0.95));
            AddColumn(new PercentileColumn("P99", 0.99));
            AddColumn(RankColumn.Arabic);
            AddColumn(BaselineRatioColumn.RatioMean);
            AddExporter(MarkdownExporter.GitHub);
        }
    }

    // ── 被测池 ──────────────────────────────────────────────
    private ObjectPool<PooledObject> _meop = null!;                   // MEOP 对照（Baseline）
    private IHayateObjectPool<PooledObject> _lean = null!;            // 全功能关闭
    private IHayateObjectPool<PooledObject> _sharded4 = null!;        // 仅 4 分片
    private IHayateObjectPool<PooledObject> _full = null!;            // 全功能开启

    private int _releaseCursor;

    [GlobalSetup]
    public void Setup()
    {
        _meop = new DefaultObjectPoolProvider { MaximumRetained = MaxPoolSize }
            .Create(new DefaultPooledObjectPolicy<PooledObject>());

        _lean = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-lean")
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

        _sharded4 = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-sharded4")
            .WithMinSize(MinPoolSize)
            .WithMaxSize(MaxPoolSize)
            .WithEnableSharding(true)
            .WithShardCount(4)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();

        _full = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-full")
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

        // 预热到 MaxPoolSize（借满再归还，触达登记 / 扩容路径），保证稳态取还不触发创建
        for (var i = 0; i < MaxPoolSize; i++) _meop.Return(_meop.Get());
        for (var i = 0; i < MaxPoolSize; i++) _lean.Release(_lean.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _sharded4.Release(_sharded4.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _full.Release(_full.Acquire());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _lean.Dispose();
        _sharded4.Dispose();
        _full.Dispose();
    }

    // ── 套 1：Acquire+Release 单线程（全部实现并列对照） ──────

    [Benchmark(Baseline = true, Description = "Acquire+Release | MEOP（基线）")]
    public void Meop_AcquireRelease()
    {
        var obj = _meop.Get();
        obj.Data++;
        _meop.Return(obj);
    }

    [Benchmark(Description = "Acquire+Release | 原生 new（无池下界）")]
    public PooledObject Native_New()
    {
        var obj = new PooledObject();
        obj.Data++;
        return obj;
    }

    [Benchmark(Description = "Acquire+Release | Hayate Lean")]
    public void Hayate_Lean_AcquireRelease()
    {
        var obj = _lean.Acquire();
        obj.Data++;
        _lean.Release(obj);
    }

    [Benchmark(Description = "Acquire+Release | Hayate Sharded4")]
    public void Hayate_Sharded_AcquireRelease()
    {
        var obj = _sharded4.Acquire();
        obj.Data++;
        _sharded4.Release(obj);
    }

    [Benchmark(Description = "Acquire+Release | Hayate Full")]
    public void Hayate_Full_AcquireRelease()
    {
        var obj = _full.Acquire();
        obj.Data++;
        _full.Release(obj);
    }

    // ── 套 2：Release 路径（归还 + 立即补位，借出集恒定） ──────

    [Benchmark(Description = "Release | MEOP Return")]
    public void Meop_Release()
    {
        var obj = _meop.Get();
        _meop.Return(obj);
    }

    [Benchmark(Description = "Release | Hayate Lean")]
    public void Hayate_Lean_Release()
    {
        var obj = _lean.Acquire();
        _lean.Release(obj);
    }

    // ── 套 3：AcquireAsync 全路径（MEOP / 原生无异步 API → N/A） ──

    [Benchmark(Description = "AcquireAsync+Release | Hayate Lean")]
    public async Task Hayate_Lean_AcquireReleaseAsync()
    {
        var obj = await _lean.AcquireAsync();
        obj.Data++;
        _lean.Release(obj);
    }

    [Benchmark(Description = "AcquireAsync+Release | Hayate Full")]
    public async Task Hayate_Full_AcquireReleaseAsync()
    {
        var obj = await _full.AcquireAsync();
        obj.Data++;
        _full.Release(obj);
    }

    // ── 套 4：并发 100 线程借还（争用口径） ────────────────────

    [Benchmark(Description = "Concurrent-100 | MEOP")]
    public void Meop_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _meop.Get();
            obj.Data++;
            _meop.Return(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | Hayate Lean")]
    public void Hayate_Lean_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _lean.Acquire();
            obj.Data++;
            _lean.Release(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | Hayate Sharded4")]
    public void Hayate_Sharded_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _sharded4.Acquire();
            obj.Data++;
            _sharded4.Release(obj);
        });
    }

    // ── 自定义分位列（BDN 无内置 P99 列；由各迭代平均耗时计算） ──

    /// <summary>
    /// M1：分位延迟列。数据源为 <see cref="BenchmarkReport.GetResultRuns"/>（正式迭代，排除预热与
    /// 工作负载试验），按迭代平均耗时升序取分位点；无数据时输出 "NA"。
    /// </summary>
    private sealed class PercentileColumn : IColumn
    {
        private readonly double _quantile;

        public PercentileColumn(string id, double quantile)
        {
            Id = id;
            _quantile = quantile;
        }

        public string Id { get; }
        public string ColumnName => Id;
        public string Legend => $"{Id} 分位延迟（各迭代平均耗时）";
        public UnitType UnitType => UnitType.Time;
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Statistics;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;

        public bool IsAvailable(Summary summary) => true;

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
            => Compute(summary, benchmarkCase);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
            => Compute(summary, benchmarkCase);

        private string Compute(Summary summary, BenchmarkCase benchmarkCase)
        {
            var runs = summary[benchmarkCase].GetResultRuns()?.ToList();
            if (runs is null || runs.Count == 0) return "NA";

            // Measurement.Nanoseconds 为「单次迭代总耗时」，需除以迭代内操作数还原单操作口径
            var ordered = runs
                .Select(r => r.Operations > 0 ? r.Nanoseconds / r.Operations : r.Nanoseconds)
                .OrderBy(v => v)
                .ToArray();

            var index = (int)Math.Ceiling(_quantile * ordered.Length) - 1;
            if (index < 0) index = 0;
            if (index >= ordered.Length) index = ordered.Length - 1;
            return ordered[index].ToString("N1", CultureInfo.InvariantCulture);
        }
    }
}

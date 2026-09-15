// HayateOP standard BenchmarkDotNet orchestration.
//
// Coverage matrix (full Acquire / Release / AcquireAsync paths):
//   * implementations under test : HayateOP Lean (wrapper-free fast path) / AllOff (general engine, every
//                                  optional feature off) / Sharded4 (sharding only) / Full
//   * reference baselines        : Microsoft.Extensions.ObjectPool (MEOP, Baseline) + plain `new` (no pool, allocation lower bound)
//   * async dimension            : MEOP and plain `new` expose no async API -> N/A (not present in the matrix)
//   * concurrency dimension      : 100-thread Parallel.For borrow/return (throughput + contention)
//
// Statistics: built-in BenchmarkDotNet columns (Mean / Median / StdDev / Min / Max) plus custom
//   P50 / P90 / P95 / P99 percentile columns (computed from the per-iteration mean time), because
//   BenchmarkDotNet ships no built-in P99 column. MemoryDiagnoser supplies the bytes-per-operation column.
//
// Run     : dotnet run -c Release -- --filter '*'
// Report  : BenchmarkDotNet.Artifacts/results/*-report-github.md (MarkdownExporter)
//           BenchmarkDotNet.Artifacts/results/*-report.csv         (CsvExporter, consumed by scripts/bench-compare.py)

using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;

BenchmarkRunner.Run<HayateOpBenchmarks>(args: args);

// Z-C-A specialized-pool allocation benchmarks (tiering benefit + generic Format helpers).
BenchmarkRunner.Run<SpecializedStringBuilderTierBenchmarks>(args: args);
BenchmarkRunner.Run<SpecializedStringBuilderFormatBenchmarks>(args: args);

// Z-BDN: five-way line-build comparison incl. Cysharp.ZString and the Z1 value builder.
BenchmarkRunner.Run<SpecializedStringComparisonBenchmarks>(args: args);

/// <summary>
/// Head-to-head benchmark matrix for HayateOP.
/// </summary>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class HayateOpBenchmarks
{
    /// <summary>Pooled object, kept structurally identical to the historical benchmark so data stays comparable.</summary>
    public class PooledObject
    {
        public int Data { get; set; }
        public void Reset() => Data = 0;
    }

    // Capacity sizing (prevents GlobalSetup from stalling): the general-engine pools are built with
    // auto-scaling disabled and warm up through a full borrow/return cycle, so the idle buffer is
    // non-empty before a benchmark starts and the create path stays out of the measurement.
    // Min = 250 keeps at least 100 idle objects available for the concurrent suite (100 borrowing
    // threads), so a blocking wait is never triggered. This matches the historical benchmark so
    // results remain comparable.
    private const int MinPoolSize = 250;
    private const int MaxPoolSize = 300;
    private const int ThreadCount = 100;
    private const int ReleaseSlots = 32;      // borrow slots of the release suite (power of two, branch-free modulo)

    private class BenchConfig : ManualConfig
    {
        public BenchConfig()
        {
            // Short job: 3 warmup + 10 iterations, keeps a full run bounded.
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
            AddExporter(CsvExporter.Default);
            AddExporter(JsonExporter.Full);
        }
    }

    // ── Pools under test ─────────────────────────────────────
    private ObjectPool<PooledObject> _meop = null!;                   // MEOP reference (Baseline)
    private IHayateObjectPool<PooledObject> _allOff = null!;          // general engine, every optional feature off
    private IHayateObjectPool<PooledObject> _lean = null!;            // lean (wrapper-free) fast path: EnableLean
    private IHayateObjectPool<PooledObject> _sharded4 = null!;        // 4 shards only
    private IHayateObjectPool<PooledObject> _full = null!;            // all features on

    private int _releaseCursor;

    [GlobalSetup]
    public void Setup()
    {
        _meop = new DefaultObjectPoolProvider { MaximumRetained = MaxPoolSize }
            .Create(new DefaultPooledObjectPolicy<PooledObject>());

        // General-purpose engine with every optional feature switched off. This is the closest the
        // sharded/wrapping engine gets to a bare pool, and it is the config the "AllOff" rows measure.
        _allOff = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-alloff")
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

        // Lean (wrapper-free) fast path. It stores the pooled value directly in a bounded array and
        // borrows/returns through Interlocked, so it drops the wrapper allocation, the shard lock
        // and every diagnostic write; feature toggles are normalized away by the builder.
        _lean = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-lean")
            .WithLean()
            .WithMinSize(MinPoolSize)
            .WithMaxSize(MaxPoolSize)
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

        // Warm up (borrow fully, then return) so the steady state never hits the create path.
        for (var i = 0; i < MaxPoolSize; i++) _meop.Return(_meop.Get());
        for (var i = 0; i < MaxPoolSize; i++) _allOff.Release(_allOff.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _lean.Release(_lean.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _sharded4.Release(_sharded4.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _full.Release(_full.Acquire());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _allOff.Dispose();
        _lean.Dispose();
        _sharded4.Dispose();
        _full.Dispose();
    }

    // ── Suite 1: single-threaded Acquire+Release (all implementations side by side) ──

    [Benchmark(Baseline = true, Description = "Acquire+Release | MEOP (baseline)")]
    [BenchmarkCategory("reference")]
    public void Meop_AcquireRelease()
    {
        var obj = _meop.Get();
        obj.Data++;
        _meop.Return(obj);
    }

    [Benchmark(Description = "Acquire+Release | plain new (no-pool lower bound)")]
    [BenchmarkCategory("reference")]
    public PooledObject Native_New()
    {
        var obj = new PooledObject();
        obj.Data++;
        return obj;
    }

    [Benchmark(Description = "Acquire+Release | Hayate AllOff")]
    [BenchmarkCategory("hot")]
    public void Hayate_AllOff_AcquireRelease()
    {
        var obj = _allOff.Acquire();
        obj.Data++;
        _allOff.Release(obj);
    }

    [Benchmark(Description = "Acquire+Release | Hayate Lean")]
    [BenchmarkCategory("hot")]
    public void Hayate_Lean_AcquireRelease()
    {
        var obj = _lean.Acquire();
        obj.Data++;
        _lean.Release(obj);
    }

    [Benchmark(Description = "Acquire+Release | Hayate Sharded4")]
    [BenchmarkCategory("hot")]
    public void Hayate_Sharded_AcquireRelease()
    {
        var obj = _sharded4.Acquire();
        obj.Data++;
        _sharded4.Release(obj);
    }

    [Benchmark(Description = "Acquire+Release | Hayate Full")]
    [BenchmarkCategory("hot")]
    public void Hayate_Full_AcquireRelease()
    {
        var obj = _full.Acquire();
        obj.Data++;
        _full.Release(obj);
    }

    // ── Suite 2: Release path (return + immediate re-borrow, borrow set held constant) ──

    [Benchmark(Description = "Release | MEOP Return")]
    [BenchmarkCategory("reference")]
    public void Meop_Release()
    {
        var obj = _meop.Get();
        _meop.Return(obj);
    }

    [Benchmark(Description = "Release | Hayate AllOff")]
    [BenchmarkCategory("hot")]
    public void Hayate_AllOff_Release()
    {
        var obj = _allOff.Acquire();
        _allOff.Release(obj);
    }

    [Benchmark(Description = "Release | Hayate Lean")]
    [BenchmarkCategory("hot")]
    public void Hayate_Lean_Release()
    {
        var obj = _lean.Acquire();
        _lean.Release(obj);
    }

    // ── Suite 3: full AcquireAsync path (MEOP / plain new have no async API -> N/A) ──

    [Benchmark(Description = "AcquireAsync+Release | Hayate AllOff")]
    [BenchmarkCategory("hot")]
    public async Task Hayate_AllOff_AcquireReleaseAsync()
    {
        var obj = await _allOff.AcquireAsync();
        obj.Data++;
        _allOff.Release(obj);
    }

    [Benchmark(Description = "AcquireAsync+Release | Hayate Lean")]
    [BenchmarkCategory("hot")]
    public async Task Hayate_Lean_AcquireReleaseAsync()
    {
        var obj = await _lean.AcquireAsync();
        obj.Data++;
        _lean.Release(obj);
    }

    [Benchmark(Description = "AcquireAsync+Release | Hayate Full")]
    [BenchmarkCategory("hot")]
    public async Task Hayate_Full_AcquireReleaseAsync()
    {
        var obj = await _full.AcquireAsync();
        obj.Data++;
        _full.Release(obj);
    }

    // ── Suite 4: 100-thread concurrent borrow/return (contention profile) ──

    [Benchmark(Description = "Concurrent-100 | MEOP")]
    [BenchmarkCategory("concurrent")]
    public void Meop_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _meop.Get();
            obj.Data++;
            _meop.Return(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | Hayate AllOff")]
    [BenchmarkCategory("concurrent")]
    public void Hayate_AllOff_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _allOff.Acquire();
            obj.Data++;
            _allOff.Release(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | Hayate Lean")]
    [BenchmarkCategory("concurrent")]
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
    [BenchmarkCategory("concurrent")]
    public void Hayate_Sharded_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _sharded4.Acquire();
            obj.Data++;
            _sharded4.Release(obj);
        });
    }

    // ── Custom percentile column (BenchmarkDotNet ships no built-in P99 column) ──

    /// <summary>
    /// Percentile latency column. Values are taken from <see cref="BenchmarkReport.GetResultRuns"/>
    /// (actual iterations only, excluding warmup and pilot runs), sorted ascending by per-iteration
    /// mean time; emits "NA" when no runs are available.
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
        public string Legend => $"{Id} percentile latency (per-iteration mean time)";
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

            // Measurement.Nanoseconds is the total time of one iteration; divide by the operation count
            // to restore the per-operation figure.
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

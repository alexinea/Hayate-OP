// HayateOP standard BenchmarkDotNet orchestration.
//
// Coverage matrix (full Acquire / Release / AcquireAsync paths):
//   * implementations under test : HayateOP Lean (wrapper-free fast path) / AllOff (general engine, every
//                                  optional feature off) / Sharded4 (sharding only) / Full
//   * reference baselines        : Microsoft.Extensions.ObjectPool (MEOP, Baseline) + plain `new` (no pool, allocation lower bound)
//                                  + marklauter MSL.Pool 7.2.1 (the N5 head-to-head line)
//                                  + RRode TinyPools 1.0.1 (the T8 head-to-head line)
//                                  + Hertzole PowerPools 1.0.0 (the O8->M1+ head-to-head line)
//                                  + Chopin.Pooling 1.0.2 (the O5->M1+ head-to-head line)
//                                  + CnCSharp-Dev PoolingLib 1.0.3 (the C-B head-to-head line)
//   * async dimension            : MEOP / plain `new` / TinyPools / PowerPools / Chopin.Pooling /
//                                  PoolingLib expose no async API
//                                  -> N/A (not present in the matrix);
//                                  MSL.Pool exposes *only* an async API -> N/A in the synchronous suites
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Pool;
using Pool.Metrics;

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

    // The asynchronous CancellationToken dimension (N5): MSL.Pool's token-carrying lease overload is
    // the one measured, so the comparison covers the "async with a CancellationToken" path. The token
    // is CancellationToken.None, so the token plumbing is exercised without a cancellation ever being
    // requested and the row stays comparable to the token-less overloads the other rows call.
    private static readonly CancellationToken LeaseCancellationToken = CancellationToken.None;

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

    // marklauter MSL.Pool 7.2.1 (N5). The library ships neither a builder nor a synchronous acquire
    // path: a pool is a `Pool<T>` constructed from an item factory, a logger and an IPoolMetrics sink,
    // and items are leased exclusively through `LeaseAsync`. It therefore appears in the release,
    // asynchronous and concurrent suites, and is N/A in the synchronous suites. Capacity basis,
    // warm-up and payload are identical to every other row so the columns stay comparable.
    private Pool<PooledObject> _msl = null!;

    // RRode TinyPools 1.0.1 (T8). The smallest pool in the matrix: a Queue of idle items behind a
    // single lock, with a per-borrow PooledObject<T> lease wrapper that hands the item back when it
    // is disposed. The library has no asynchronous API and no min-size, timeout, metrics or
    // eviction surface at all, so it appears in the synchronous and concurrent suites and is N/A in
    // the asynchronous one. Its capacity constructor argument is a maximum-retained bound, which is
    // the same retention semantics the MEOP reference row uses.
    private TinyPools.ObjectPool<PooledObject> _tinyPools = null!;

    // Hertzole PowerPools 1.0.0 (O8->M1+). The library ships a single elastic pool type plus
    // fixed-size and collection-pool variants; ObjectPool<T> is the direct counterpart of the
    // general engine because it is the only one that takes an explicit item factory, and the README
    // states its storage is ArrayPool-backed - the same storage strategy the HayateOP ArrayPool (O-D)
    // row uses, which is what makes this column a like-for-like comparison rather than a mismatch.
    // It is constructed through the static factory (the type is sealed and has no public
    // constructor), exposes a synchronous Rent/Return pair and no asynchronous API at all, so it
    // appears in the synchronous, release and concurrent suites and is N/A in the asynchronous one,
    // exactly like MEOP, plain `new` and TinyPools. OnReturn/onDispose callbacks are left unset:
    // the matrix's other rows carry no per-object callback either, and the default capacity is
    // overridden explicitly below so the capacity basis matches every other row.
    private Hertzole.PowerPools.ObjectPool<PooledObject> _powerPools = null!;

    // Chopin.Pooling 1.0.2 (O5->M1+). A port of the Apache Commons Pool API: a GenericObjectPool<T>
    // is built from an IPooledObjectFactory<T> plus a GenericObjectPoolConfig, and items move through
    // a synchronous BorrowObject/ReturnObject pair with no asynchronous API at all. It therefore sits
    // in the synchronous, release and concurrent suites and is N/A in the asynchronous one, exactly
    // like MEOP, plain `new`, TinyPools and PowerPools. MinIdle/MaxIdle carry the same 250/300
    // capacity basis as every other row. The port keeps Commons Pool's object-tracking dictionary,
    // which is what makes this column worth measuring: ReturnObject allocates a fresh lookup key on
    // every single return, so the pair is not allocation-free even at steady state.
    private Chopin.Pooling.Impl.GenericObjectPool<PooledObject> _chopin = null!;

    // CnCSharp-Dev PoolingLib 1.0.3 (C-B). The leanest library in the matrix: a plain
    // ConcurrentQueue<T> holding the pooled value directly, with no wrapper node, no per-borrow or
    // per-return timestamp, no counter, no background timer and no capacity bound whatsoever. It
    // has a synchronous Get/Return pair and no asynchronous API at all, so it sits in the
    // synchronous, release and concurrent suites and is N/A in the asynchronous one, exactly like
    // MEOP, plain `new`, TinyPools, PowerPools and Chopin.Pooling. BasePool<T> is the library's
    // only plain object-pool type and its constructor is protected, so the sole public entry point
    // is the static Pool property - one instance per closed generic type (verified: repeated reads
    // return the same reference). A derived type would add nothing to the measured path, so the
    // static instance is used directly. The library also exposes an obsolete Release() method
    // explicitly marked "renamed to Return"; the canonical Return is used instead. Note that
    // BasePool<T>.Return does not reset the object - only the collection-specialized pools clear
    // their payload - which is level with every other row, none of which resets either.
    private PoolingLib.BasePool<PooledObject> _cpl = null!;

    private IHayateObjectPool<PooledObject> _allOff = null!;          // general engine, every optional feature off
    private IHayateObjectPool<PooledObject> _lean = null!;            // lean (wrapper-free) fast path: EnableLean
    private IHayateObjectPool<PooledObject> _ap = null!;              // lean + ArrayPool direct-storage backend (O-D)
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

        _ap = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-arraypool")
            .WithArrayPoolStorage()
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

        // marklauter MSL.Pool (N5), built through the public constructor - the library's DI extension
        // methods add nothing to the measured path. Same capacity basis as every other row
        // (Min 250 / Max 300), and the defaults are already "no optional feature on": no preparation
        // strategy is supplied, so the lease route is the plain "take an idle item, or create one
        // while below MaxSize" path. The two infinite defaults (LeaseTimeout and IdleTimeout are
        // Timeout.InfiniteTimeSpan out of the box: no lease timeout, no lazy idle eviction) are set
        // explicitly so a future default change cannot silently alter what this row measures.
        _msl = new Pool<PooledObject>(
            PooledObjectFactory.Instance,
            NullLogger<Pool<PooledObject>>.Instance,
            NoOpPoolMetrics.Instance,
            new PoolOptions
            {
                MinSize = MinPoolSize,
                MaxSize = MaxPoolSize,
                LeaseTimeout = Timeout.InfiniteTimeSpan,
                IdleTimeout = Timeout.InfiniteTimeSpan,
            },
            TimeProvider.System);

        // RRode TinyPools (T8), built through the public capacity-taking constructor. The library has
        // no min-size notion: the capacity argument bounds how many objects it retains and anything
        // returned beyond that bound is dropped for garbage collection, which is exactly the
        // contract of the MEOP row's MaximumRetained. MaxPoolSize is therefore the capacity basis.
        // The warm-up line below uses the same borrow/return idiom as every other row; note that for
        // this library that idiom leaves one idle item rather than MaxPoolSize, and the steady state
        // (create path out of the measurement) is established by BenchmarkDotNet's own warm-up
        // iterations - verified in docs/benchmarks/2026-09-18-t8-tinypools-line.md.
        _tinyPools = new TinyPools.ObjectPool<PooledObject>(() => new PooledObject(), MaxPoolSize);

        // Hertzole PowerPools (O8->M1+), built through the public static factory. The library has no
        // min-size notion, so initialCapacity takes MaxPoolSize - the same retention/capacity basis
        // the MEOP row's MaximumRetained and the TinyPools row's capacity argument use. The requested
        // size is not what Capacity reports back (initialCapacity: 300 reads back as Capacity 512,
        // observed in the steady-state probe); the rounding is upward, so it never constrains the
        // borrow set. The optional onRent/onReturn/onDispose callbacks are left at their null
        // defaults and initialCapacity is pinned explicitly rather than relying on the library
        // default of 16, so a future default change cannot silently alter this row.
        _powerPools = Hertzole.PowerPools.ObjectPool<PooledObject>.Create(
            () => new PooledObject(),
            initialCapacity: MaxPoolSize);

        // Chopin.Pooling (O5->M1+), built through the public constructor from an
        // IPooledObjectFactory<T> plus a GenericObjectPoolConfig. Every option that could add work to
        // the borrow/return path is pinned to its explicit value instead of being left to the library
        // default, so a future upstream default change cannot silently alter this row: no validation
        // on create / borrow / return / idle, and no eviction runs at all
        // (TimeBetweenEvictionRunsMillis = -1). MinIdle / MaxIdle mirror the MinPoolSize / MaxPoolSize
        // capacity basis every other row uses. Observed on construction, this implementation does not
        // pre-fill to MinIdle, so like the rest of the matrix the pool is warmed by the loop below.
        // MaxWaitMillis = -1 is the library's "wait indefinitely" (BlockWhenExhausted defaults to
        // true and is pinned here anyway). BorrowStrategy.LIFO plus pool.Lifo = true is the library
        // default and the counterpart of every other row's "most recently returned item first" fast
        // path.
        _chopin = new Chopin.Pooling.Impl.GenericObjectPool<PooledObject>(
            ChopinPooledObjectFactory.Instance,
            new Chopin.Pooling.Impl.GenericObjectPoolConfig
            {
                MaxTotal = MaxPoolSize,
                MaxIdle = MaxPoolSize,
                MinIdle = MinPoolSize,
                BlockWhenExhausted = true,
                MaxWaitMillis = -1,
                TestOnCreate = false,
                TestOnBorrow = false,
                TestOnReturn = false,
                TestWhileIdle = false,
                TimeBetweenEvictionRunsMillis = -1,
                NumTestsPerEvictionRun = 3,
                MinEvictableIdleTimeMillis = 1800000,
                SoftMinEvictableIdleTimeMillis = -1,
                BorrowStrategy = Chopin.Pooling.Impl.BorrowStrategy.LIFO,
            });
        _chopin.Lifo = true;

        // CnCSharp-Dev PoolingLib (C-B). This library offers no configuration surface at all: no
        // capacity or maximum-retained bound, no min size, no timeout, no metrics sink, no logger and
        // no eviction timer - so there is nothing to pin and nothing to switch off, which is itself
        // part of what this column records. The single capacity-relevant fact is that the return path
        // is unbounded (every returned object is enqueued and never dropped), i.e. precisely the
        // unbounded anti-pattern the CPL comparison report told the O-D storage backend to avoid.
        _cpl = PoolingLib.BasePool<PooledObject>.Pool;

        // Warm up (borrow fully, then return) so the steady state never hits the create path.
        for (var i = 0; i < MaxPoolSize; i++) _meop.Return(_meop.Get());
        for (var i = 0; i < MaxPoolSize; i++) _allOff.Release(_allOff.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _lean.Release(_lean.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _ap.Release(_ap.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _sharded4.Release(_sharded4.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _full.Release(_full.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _msl.Release(_msl.LeaseAsync().GetAwaiter().GetResult());
        for (var i = 0; i < MaxPoolSize; i++) _tinyPools.GetObject().Dispose();
        // Same single-loop idiom as every other row, on purpose: PowerPools also offers PreWarm(int),
        // but using it here would give this row a different warm-up contract from the rest of the
        // matrix. As with TinyPools the idiom leaves one idle item rather than MaxPoolSize, and the
        // steady state is established by BenchmarkDotNet's own warm-up iterations; the factory-call
        // delta across 1,000,000 borrow/return pairs and a 100-thread burst is 0, verified in
        // docs/benchmarks/2026-09-18-o8-powerpools-line.md.
        for (var i = 0; i < MaxPoolSize; i++) _powerPools.Return(_powerPools.Rent());
        // Same single-loop idiom again. For Chopin.Pooling the idiom leaves one idle item rather than
        // MaxPoolSize too (observed: NumIdle = 1, created = 1 after the warm-up), and the steady state
        // - create path out of the measurement window - is established by BenchmarkDotNet's own warm-up
        // iterations, verified in docs/benchmarks/2026-09-18-o5-chopin-line.md.
        for (var i = 0; i < MaxPoolSize; i++) _chopin.ReturnObject(_chopin.BorrowObject());
        // Same single-loop idiom again. PoolingLib stores whatever it is given and drops nothing, so
        // the warm-up leaves one idle item rather than MaxPoolSize, and the steady state - create
        // path out of the measurement window - is established by BenchmarkDotNet's own warm-up
        // iterations, verified in docs/benchmarks/2026-09-18-cb-poolinglib-line.md.
        for (var i = 0; i < MaxPoolSize; i++) _cpl.Return(_cpl.Get());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _allOff.Dispose();
        _lean.Dispose();
        _ap.Dispose();
        _sharded4.Dispose();
        _full.Dispose();
        _msl.Dispose();
        _powerPools.Dispose();
        // Chopin.Pooling's GenericObjectPool<T> is not IDisposable either; Close() is its teardown
        // verb (it clears the idle set and stops the evictor, which this row never starts).
        _chopin.Close();
        // TinyPools' ObjectPool<T> is not IDisposable: it holds nothing that needs releasing.
        // PoolingLib's BasePool<T> is not IDisposable either and exposes no teardown verb at all: the
        // static instance simply keeps its idle ConcurrentQueue<T> alive for the life of the process.
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

    // TinyPools leases through a wrapper that returns the item on Dispose; it has a synchronous
    // acquire path but no asynchronous one, the mirror image of MEOP and plain `new` being absent
    // from the asynchronous suite.
    [Benchmark(Description = "Acquire+Release | TinyPools")]
    [BenchmarkCategory("reference")]
    public void TinyPools_AcquireRelease()
    {
        var lease = _tinyPools.GetObject();
        lease.Object.Data++;
        lease.Dispose();
    }

    // PowerPools has a synchronous Rent/Return pair and no asynchronous API, so it sits in the
    // synchronous suites. Rent/Return is its direct counterpart of Acquire/Release, and the payload
    // mutation matches every other row so the pooled-object cost is identical across columns.
    [Benchmark(Description = "Acquire+Release | PowerPools")]
    [BenchmarkCategory("reference")]
    public void PowerPools_AcquireRelease()
    {
        var obj = _powerPools.Rent();
        obj.Data++;
        _powerPools.Return(obj);
    }

    // Chopin.Pooling is the only column that keeps a Commons-Pool-style identity dictionary on the
    // return path, so it belongs next to the other synchronous reference rows: BorrowObject/ReturnObject
    // is its direct counterpart of Acquire/Release, and the payload mutation matches every other row so
    // the pooled-object cost is identical across columns.
    [Benchmark(Description = "Acquire+Release | Chopin.Pooling")]
    [BenchmarkCategory("reference")]
    public void Chopin_AcquireRelease()
    {
        var obj = _chopin.BorrowObject();
        obj.Data++;
        _chopin.ReturnObject(obj);
    }

    // PoolingLib stores the value directly in a ConcurrentQueue<T> with no wrapper, no timestamp, no
    // counter and no capacity check, so it belongs next to the other synchronous reference rows:
    // Get/Return is its direct counterpart of Acquire/Release, and the payload mutation matches every
    // other row so the pooled-object cost is identical across columns.
    [Benchmark(Description = "Acquire+Release | PoolingLib")]
    [BenchmarkCategory("reference")]
    public void PoolingLib_AcquireRelease()
    {
        var obj = _cpl.Get();
        obj.Data++;
        _cpl.Return(obj);
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

    [Benchmark(Description = "Acquire+Release | Hayate ArrayPool (O-D)")]
    [BenchmarkCategory("hot")]
    public void Hayate_ArrayPool_AcquireRelease()
    {
        var obj = _ap.Acquire();
        obj.Data++;
        _ap.Release(obj);
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

    [Benchmark(Description = "Release | MSL.Pool")]
    [BenchmarkCategory("reference")]
    public async Task Msl_Release()
    {
        var obj = await _msl.LeaseAsync();
        _msl.Release(obj);
    }

    [Benchmark(Description = "Release | TinyPools")]
    [BenchmarkCategory("reference")]
    public void TinyPools_Release()
    {
        var lease = _tinyPools.GetObject();
        lease.Dispose();
    }

    [Benchmark(Description = "Release | PowerPools")]
    [BenchmarkCategory("reference")]
    public void PowerPools_Release()
    {
        var obj = _powerPools.Rent();
        _powerPools.Return(obj);
    }

    [Benchmark(Description = "Release | Chopin.Pooling")]
    [BenchmarkCategory("reference")]
    public void Chopin_Release()
    {
        var obj = _chopin.BorrowObject();
        _chopin.ReturnObject(obj);
    }

    [Benchmark(Description = "Release | PoolingLib")]
    [BenchmarkCategory("reference")]
    public void PoolingLib_Release()
    {
        var obj = _cpl.Get();
        _cpl.Return(obj);
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

    // MSL.Pool is async-only; its token-carrying overload is the one measured (see the field comment
    // on LeaseCancellationToken), which is the marklauter counterpart of the rows above.
    [Benchmark(Description = "AcquireAsync+Release | MSL.Pool (CT)")]
    [BenchmarkCategory("reference")]
    public async Task Msl_AcquireReleaseAsync()
    {
        var obj = await _msl.LeaseAsync(LeaseCancellationToken);
        obj.Data++;
        _msl.Release(obj);
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

    [Benchmark(Description = "Concurrent-100 | MSL.Pool")]
    [BenchmarkCategory("concurrent")]
    public void Msl_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            // MSL.Pool is async-only. On a warm pool the lease completes synchronously, so the wait
            // bottoms out in the pool's own interlocked fast path rather than in a thread-pool hop.
            var obj = _msl.LeaseAsync().GetAwaiter().GetResult();
            obj.Data++;
            _msl.Release(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | TinyPools")]
    [BenchmarkCategory("concurrent")]
    public void TinyPools_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var lease = _tinyPools.GetObject();
            lease.Object.Data++;
            lease.Dispose();
        });
    }

    [Benchmark(Description = "Concurrent-100 | PowerPools")]
    [BenchmarkCategory("concurrent")]
    public void PowerPools_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _powerPools.Rent();
            obj.Data++;
            _powerPools.Return(obj);
        });
    }

    [Benchmark(Description = "Concurrent-100 | Chopin.Pooling")]
    [BenchmarkCategory("concurrent")]
    public void Chopin_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _chopin.BorrowObject();
            obj.Data++;
            _chopin.ReturnObject(obj);
        });
    }

    // PoolingLib's single ConcurrentQueue<T> is the one unbounded, unsynchronized-by-caller store in
    // the matrix, so the concurrent row is what exposes whether its lock-free queue absorbs 100-thread
    // contention better or worse than the matrix's sharded and plain-locked rows.
    [Benchmark(Description = "Concurrent-100 | PoolingLib")]
    [BenchmarkCategory("concurrent")]
    public void PoolingLib_Concurrent100()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _cpl.Get();
            obj.Data++;
            _cpl.Return(obj);
        });
    }

    // ── MSL.Pool (marklauter) adapters ───────────────────────

    /// <summary>
    /// Item factory handed to the marklauter pool. It produces the same payload class every other row
    /// leases, so the pooled-object cost is identical across the columns.
    /// </summary>
    private sealed class PooledObjectFactory : IItemFactory<PooledObject>
    {
        public static readonly PooledObjectFactory Instance = new();

        private PooledObjectFactory()
        {
        }

        public PooledObject CreateItem() => new PooledObject();
    }

    /// <summary>
    /// Metrics-disabled <c>IPoolMetrics</c> sink. MSL.Pool records into <c>System.Diagnostics.Metrics</c>
    /// by default; dropping the recording keeps this row level with the HayateOP configurations that
    /// run with metrics off (<c>WithEnableMetrics(false)</c>) or on the lean storage, and keeps the
    /// observable-gauge registrations out of the measured path.
    /// </summary>
    private sealed class NoOpPoolMetrics : IPoolMetrics
    {
        public static readonly NoOpPoolMetrics Instance = new();

        private NoOpPoolMetrics()
        {
        }

        public void RecordLeaseException(Exception ex)
        {
        }

        public void RecordPreparationException(Exception ex)
        {
        }

        public void RecordLeaseWaitTime(TimeSpan duration)
        {
        }

        public void RecordPreparationTime(TimeSpan duration)
        {
        }

        public IDisposable RegisterItemsAllocatedObserver(Func<int> observeValue) => NullSubscription.Instance;

        public IDisposable RegisterItemsAvailableObserver(Func<int> observeValue) => NullSubscription.Instance;

        public IDisposable RegisterActiveLeasesObserver(Func<int> observeValue) => NullSubscription.Instance;

        public IDisposable RegisterQueuedLeasesObserver(Func<int> observeValue) => NullSubscription.Instance;

        public IDisposable RegisterUtilizationRateObserver(Func<double> observeValue) => NullSubscription.Instance;

        /// <summary>Shared no-op handle for the observer registrations the metrics sink ignores.</summary>
        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();

            private NullSubscription()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    // ── Chopin.Pooling (labijie) adapters ────────────────────

    /// <summary>
    /// Object factory handed to the Chopin.Pooling pool. <c>Create()</c> produces the same payload class
    /// every other row uses, so the pooled-object cost is identical across the columns, and
    /// <c>Wrap()</c> returns the library's own <c>DefaultPooledObject&lt;T&gt;</c> - the pairing the
    /// port's base factory documents as the default. The library requires a factory instance, so this
    /// is the one adapter the Chopin.Pooling column needs; there is no metrics or logging surface to
    /// neutralize on the borrow/return path.
    /// </summary>
    private sealed class ChopinPooledObjectFactory : Chopin.Pooling.BasePooledObjectFactory<PooledObject>
    {
        public static readonly ChopinPooledObjectFactory Instance = new();

        private ChopinPooledObjectFactory()
        {
        }

        public override PooledObject Create() => new();

        public override Chopin.Pooling.IPooledObject<PooledObject> Wrap(PooledObject obj)
            => new Chopin.Pooling.Impl.DefaultPooledObject<PooledObject>(obj);
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

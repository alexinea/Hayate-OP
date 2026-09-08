// T11 — HayateOP 基准测试（PR-C，2026-09-08）
// 六套基准：Acquire_Default / Acquire_WithValidation / Acquire_Sharded_4 /
//           Release_Only / Mixed_AcquireRelease_50_50 / Concurrent_Acquire_100Threads
// 对照基线：Microsoft.Extensions.ObjectPool.DefaultObjectPool<T>（Baseline = true）。
// 短迭代 Job（3 预热 + 10 迭代）防本地/CI 挂起；报告输出 BenchmarkDotNet.Artifacts/results/。

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Running;
using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;

var summary = BenchmarkRunner.Run<HayateOpBenchmark>();

[Config(typeof(BenchmarkConfig))]
public class HayateOpBenchmark
{
    private class PooledObject
    {
        public int Data { get; set; }
        public void Reset() => Data = 0;
    }

    // 容量配比说明（防 GlobalSetup 卡死）：Hayate 极简/分片池 autoScaling=false，
    // 池空不自动创建（Known Limitation）。Mixed 常驻借出 50 + ReleaseSlots 借出 32 = 82；
    // Min=250 保证套 6（100 线程并发借还）仍有 ≥100 空闲对象可用，绝不触发 Block 等待。
    private const int MinPoolSize = 250;
    private const int MaxPoolSize = 300;
    private const int ThreadCount = 100;
    private const int BorrowedHalf = MinPoolSize / 5;   // Mixed 50/50 的常驻借出深度（50）
    private const int ReleaseSlots = 32;      // Release_Only 的借出槽位（2 的幂，取模免分支）

    private class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            // 短迭代 Job：3 次预热 + 10 次正式迭代，防止长跑挂起（T11 硬约束）
            AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcServer(true)
                .WithGcConcurrent(true));

            AddDiagnoser(MemoryDiagnoser.Default);
            AddDiagnoser(ThreadingDiagnoser.Default);
            AddColumn(RankColumn.Arabic);
            AddColumn(BaselineRatioColumn.RatioMean);
        }
    }

    // ── 被测池 ──────────────────────────────────────────────
    private ObjectPool<PooledObject> _msPool = null!;              // MEOOP 对照基线
    private IHayateObjectPool<PooledObject> _hayateMinimal = null!; // 全功能关闭（与 MEOOP 对齐）
    private IHayateObjectPool<PooledObject> _hayateSharded4 = null!; // 仅开 4 分片
    private IHayateObjectPool<PooledObject> _hayateFull = null!;    // 全功能开启

    // Mixed 50/50 的常驻借出栈（每迭代 1 次 Release + 1 次 Acquire，净中性）
    private readonly ConcurrentStack<PooledObject> _mixedMs = new();
    private readonly ConcurrentStack<PooledObject> _mixedMinimal = new();
    private readonly ConcurrentStack<PooledObject> _mixedSharded = new();

    // Release_Only 的借出槽位（Release 后立即补位 Acquire，维持借出集恒定）
    private PooledObject[] _releaseSlotsMs = null!;
    private PooledObject[] _releaseSlotsMinimal = null!;
    private PooledObject[] _releaseSlotsSharded = null!;
    private int _releaseCursor;

    [GlobalSetup]
    public void Setup()
    {
        _msPool = new DefaultObjectPoolProvider { MaximumRetained = MaxPoolSize }
            .Create(new DefaultPooledObjectPolicy<PooledObject>());

        _hayateMinimal = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-minimal")
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

        _hayateSharded4 = new HayatePoolBuilder<PooledObject>()
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

        _hayateFull = new HayatePoolBuilder<PooledObject>()
            .WithPoolName("bench-full-feature")
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

        // 预热：把各池填充到 MinPoolSize（借满再归还触发登记/扩容路径）
        for (var i = 0; i < MaxPoolSize; i++) _msPool.Return(_msPool.Get());
        for (var i = 0; i < MaxPoolSize; i++) _hayateMinimal.Release(_hayateMinimal.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _hayateSharded4.Release(_hayateSharded4.Acquire());
        for (var i = 0; i < MaxPoolSize; i++) _hayateFull.Release(_hayateFull.Acquire());

        // Mixed 50/50：各池常驻借出 BorrowedHalf 个
        FillStack(_mixedMs, () => _msPool.Get());
        FillStack(_mixedMinimal, () => _hayateMinimal.Acquire());
        FillStack(_mixedSharded, () => _hayateSharded4.Acquire());

        // Release_Only：各池借出 ReleaseSlots 个占槽
        _releaseSlotsMs = FillSlots(() => _msPool.Get());
        _releaseSlotsMinimal = FillSlots(() => _hayateMinimal.Acquire());
        _releaseSlotsSharded = FillSlots(() => _hayateSharded4.Acquire());
    }

    private static void FillStack(ConcurrentStack<PooledObject> stack, Func<PooledObject> acquire)
    {
        for (var i = 0; i < BorrowedHalf; i++) stack.Push(acquire());
    }

    private PooledObject[] FillSlots(Func<PooledObject> acquire)
    {
        var slots = new PooledObject[ReleaseSlots];
        for (var i = 0; i < ReleaseSlots; i++) slots[i] = acquire();
        return slots;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hayateMinimal.Dispose();
        _hayateSharded4.Dispose();
        _hayateFull.Dispose();
    }

    // ── 套 1：Acquire_Default（单线程，MEOOP 为基线） ──────────
    [Benchmark(Baseline = true, Description = "套1 Acquire_Default | MEOOP Get（基线）")]
    public void Microsoft_Acquire_Default()
    {
        var obj = _msPool.Get();
        obj.Data++;
        _msPool.Return(obj);
    }

    [Benchmark(Description = "套1 Acquire_Default | Hayate 极简")]
    public void Hayate_Acquire_Default_Minimal()
    {
        var obj = _hayateMinimal.Acquire();
        obj.Data++;
        _hayateMinimal.Release(obj);
    }

    // ── 套 2：Acquire_WithValidation（全功能：校验+指标+泄漏检测+驱逐） ──
    [Benchmark(Description = "套2 Acquire_WithValidation | Hayate 全功能")]
    public void Hayate_Acquire_WithValidation_FullFeature()
    {
        var obj = _hayateFull.Acquire();
        obj.Data++;
        _hayateFull.Release(obj);
    }

    // ── 套 3：Acquire_Sharded_4（仅开 4 分片，其余关闭） ──
    [Benchmark(Description = "套3 Acquire_Sharded_4 | Hayate 4分片")]
    public void Hayate_Acquire_Sharded_4()
    {
        var obj = _hayateSharded4.Acquire();
        obj.Data++;
        _hayateSharded4.Release(obj);
    }

    // ── 套 4：Release_Only（Release + 立即补位 Acquire，借出集恒定） ──
    [Benchmark(Description = "套4 Release_Only | MEOOP Return")]
    public void Microsoft_Release_Only()
    {
        var slot = _releaseCursor++ & (ReleaseSlots - 1);
        _msPool.Return(_releaseSlotsMs[slot]);
        _releaseSlotsMs[slot] = _msPool.Get();
    }

    [Benchmark(Description = "套4 Release_Only | Hayate 极简")]
    public void Hayate_Release_Only_Minimal()
    {
        var slot = _releaseCursor++ & (ReleaseSlots - 1);
        _hayateMinimal.Release(_releaseSlotsMinimal[slot]);
        _releaseSlotsMinimal[slot] = _hayateMinimal.Acquire();
    }

    // ── 套 5：Mixed_AcquireRelease_50_50（每迭代 1 还 + 1 借，50/50 交错） ──
    [Benchmark(Description = "套5 Mixed_50_50 | MEOOP")]
    public void Microsoft_Mixed_50_50()
    {
        if (_mixedMs.TryPop(out var obj)) _msPool.Return(obj);
        _mixedMs.Push(_msPool.Get());
    }

    [Benchmark(Description = "套5 Mixed_50_50 | Hayate 极简")]
    public void Hayate_Mixed_50_50_Minimal()
    {
        if (_mixedMinimal.TryPop(out var obj)) _hayateMinimal.Release(obj);
        _mixedMinimal.Push(_hayateMinimal.Acquire());
    }

    [Benchmark(Description = "套5 Mixed_50_50 | Hayate 4分片")]
    public void Hayate_Mixed_50_50_Sharded4()
    {
        if (_mixedSharded.TryPop(out var obj)) _hayateSharded4.Release(obj);
        _mixedSharded.Push(_hayateSharded4.Acquire());
    }

    // ── 套 6：Concurrent_Acquire_100Threads（Parallel 100 线程借还） ──
    [Benchmark(Description = "套6 Concurrent_100Threads | MEOOP")]
    public void Microsoft_Concurrent_100Threads()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _msPool.Get();
            obj.Data++;
            _msPool.Return(obj);
        });
    }

    [Benchmark(Description = "套6 Concurrent_100Threads | Hayate 极简")]
    public void Hayate_Concurrent_100Threads_Minimal()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _hayateMinimal.Acquire();
            obj.Data++;
            _hayateMinimal.Release(obj);
        });
    }

    [Benchmark(Description = "套6 Concurrent_100Threads | Hayate 4分片")]
    public void Hayate_Concurrent_100Threads_Sharded4()
    {
        Parallel.For(0, ThreadCount, _ =>
        {
            var obj = _hayateSharded4.Acquire();
            obj.Data++;
            _hayateSharded4.Release(obj);
        });
    }
}

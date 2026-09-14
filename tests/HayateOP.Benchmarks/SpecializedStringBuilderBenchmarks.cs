// Z-C-A allocation benchmarks for the specialized pools (MemoryDiagnoser, allocation column is the
// acceptance lens):
//   * TierBenchmarks  — quantifies what declared-capacity borrows save on a mixed-size workload. The
//                       default borrow grows a builder past the pool's destroy threshold, so every
//                       operation rebuilds one from scratch; the declared borrow routes to a capacity
//                       tier whose builder is parked and reused. The plain-new row is the no-pool
//                       allocation lower bound.
//   * FormatBenchmarks— quantifies the generic no-params helpers against string.Format: no object[],
//                       no boxing, one borrow/return cycle.
//
// Run: dotnet run -c Release --project tests/HayateOP.Benchmarks -- --filter '*Specialized*'

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using DotNetCore.HayateOP.Specialized;
using System.Text;

/// <summary>
/// Declared-capacity tiering on <see cref="StringBuilderPool"/>: allocation per operation for a build
/// whose size the default borrow cannot retain (grow → destroy → rebuild churn) against the same build
/// served by a capacity tier.
/// </summary>
[Config(typeof(TierBenchConfig))]
[MemoryDiagnoser]
public class SpecializedStringBuilderTierBenchmarks
{
    // 300K chars, comfortably above the 256K destroy threshold set in Setup: the default path cannot
    // park what it grew, the declared path can.
    private const int LargePayloadChars = 300 * 1024;

    private class TierBenchConfig : ManualConfig
    {
        public TierBenchConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcServer(true)
                .WithGcConcurrent(true)
                .WithId("Short"));
            AddColumn(StatisticColumn.StdDev);
            AddColumn(RankColumn.Arabic);
            AddColumn(BaselineRatioColumn.RatioMean);
            AddExporter(MarkdownExporter.GitHub);
            AddExporter(CsvExporter.Default);
        }
    }

    private StringBuilderPool _pool = null!;
    private string _largePayload = null!;
    private string _smallPayload = "order-12345";

    [GlobalSetup]
    public void Setup()
    {
        _pool = new StringBuilderPool(4);
        // The destroy threshold sits below the large payload, which is what creates the churn for the
        // default borrow.
        _pool.MaximumStringBuilderCapacity = 256 * 1024;
        _largePayload = new string('x', LargePayloadChars);

        // Warm the engines once (tier creation + first builder) so the measurement is steady state.
        var warm = _pool.GetObject(300_000);
        warm.Dispose();
        warm = _pool.GetObject();
        warm.Dispose();
    }

    /// <summary>
    /// The bare borrow/return cycle on the ordinary engine: this fixed bookkeeping cost sits inside
    /// every other row and explains why the small builds report ~432 B — the engine's borrow path,
    /// not the builder work.
    /// </summary>
    [Benchmark(Description = "Borrow+return cycle only (engine bookkeeping floor)")]
    public string BorrowCycleOnly()
    {
        var sb = _pool.GetObject();
        sb.Dispose();
        return string.Empty;
    }

    [Benchmark(Baseline = true, Description = "300K build | default borrow (grow → destroy → rebuild)")]
    public string LargeBuild_DefaultBorrow()
    {
        var sb = _pool.GetObject();
        sb.StringBuilder.Append(_largePayload);
        return sb.ToStringReturn();
    }

    [Benchmark(Description = "300K build | declared-capacity borrow (tier parks the builder)")]
    public string LargeBuild_DeclaredCapacity()
    {
        var sb = _pool.GetObject(300_000);
        sb.StringBuilder.Append(_largePayload);
        return sb.ToStringReturn();
    }

    [Benchmark(Description = "300K build | plain new StringBuilder (no-pool lower bound)")]
    public string LargeBuild_PlainNew()
    {
        var sb = new StringBuilder(300_000);
        sb.Append(_largePayload);
        return sb.ToString();
    }

    [Benchmark(Description = "11-char build | default borrow")]
    public string SmallBuild_DefaultBorrow()
    {
        var sb = _pool.GetObject();
        sb.StringBuilder.Append(_smallPayload);
        return sb.ToStringReturn();
    }

    [Benchmark(Description = "11-char build | declared 16 chars (base tier serves it)")]
    public string SmallBuild_DeclaredCapacity()
    {
        var sb = _pool.GetObject(16);
        sb.StringBuilder.Append(_smallPayload);
        return sb.ToStringReturn();
    }
}

/// <summary>
/// The generic no-params helpers against <see cref="string.Format(string, object, object)"/>: the
/// generic path carries no <c>object[]</c> and boxes nothing, and the pooled builder skips the
/// per-call builder allocation.
/// </summary>
[Config(typeof(FormatBenchConfig))]
[MemoryDiagnoser]
public class SpecializedStringBuilderFormatBenchmarks
{
    private class FormatBenchConfig : ManualConfig
    {
        public FormatBenchConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcServer(true)
                .WithGcConcurrent(true)
                .WithId("Short"));
            AddColumn(StatisticColumn.StdDev);
            AddColumn(RankColumn.Arabic);
            AddColumn(BaselineRatioColumn.RatioMean);
            AddExporter(MarkdownExporter.GitHub);
            AddExporter(CsvExporter.Default);
        }
    }

    private const string Format2 = "{0}-{1}";
    private const string Format3 = "{0}-{1}-{2}";

    private string _text = "order";
    private int _number = 42;
    private double _amount = 1234.5678;

    /// <summary>
    /// The bare borrow/return cycle on the ordinary engine. This fixed bookkeeping cost sits inside
    /// every pooled row, so the pooled Format rows read as this floor plus the final string — the
    /// honest comparison against string.Format is the helper's own writes, which add no object[] and
    /// no boxing on top of the cycle.
    /// </summary>
    [Benchmark(Description = "Borrow+return cycle only (engine bookkeeping floor)")]
    public string BorrowCycleOnly()
    {
        var sb = StringBuilderPool.Instance.GetObject();
        sb.Dispose();
        return string.Empty;
    }

    [Benchmark(Baseline = true, Description = "Format 2 args | string.Format (object[] + boxing)")]
    public string StringFormat2() => string.Format(Format2, _text, _number);

    [Benchmark(Description = "Format 2 args | StringBuilderPool.Format (generic, pooled)")]
    public string PoolFormat2() => StringBuilderPool.Format(Format2, _text, _number);

    [Benchmark(Description = "Format 3 args | string.Format (object[] + boxing)")]
    public string StringFormat3() => string.Format(Format3, _text, _number, _amount);

    [Benchmark(Description = "Format 3 args | StringBuilderPool.Format (generic, pooled)")]
    public string PoolFormat3() => StringBuilderPool.Format(Format3, _text, _number, _amount);
}

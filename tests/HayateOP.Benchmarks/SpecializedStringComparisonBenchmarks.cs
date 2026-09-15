// Z-BDN: the five-way string-building comparison column (sbpool performance assessment, execution
// order Z1 → Z-BDN). One representative line build — "order: 42 units" — across every approach the
// ecosystem offers:
//
//   string.Format            the BCL baseline (object[] + boxing on every call)
//   StringBuilder direct     a fresh StringBuilder per operation, no pooling
//   StringBuilderPool        the O-B pooled builder (borrow → append → ToStringReturn)
//   StringBuilderPool.Format the Z-C-A generic no-params helper (one borrow/return cycle)
//   HayateValueStringBuilder the Z1 zero-allocation ref struct (ArrayPool<char> buffer)
//   ZString                  Cysharp.ZString's Utf16ValueStringBuilder / ZString.Format
//   SharedStringBuilder      the O-B shared lock-guarded builder (Build callback path)
//
// The numeric argument is an int on purpose: ZString writes it straight into its buffer, while the
// HayateOP surfaces still materialize one small intermediate string per argument — exactly the gap
// Z4a (TryFormat direct-write) exists to close, so this table doubles as the before picture for it.
//
// Run: dotnet run -c Release --project tests/HayateOP.Benchmarks -- --filter '*SpecializedStringComparison*'

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using Cysharp.Text;
using DotNetCore.HayateOP.Specialized;
using System.Text;

/// <summary>
/// The five-way line-build comparison: identical content, every approach side by side, allocation
/// columns included (MemoryDiagnoser) so zero-allocation claims show up as data.
/// </summary>
[Config(typeof(ComparisonBenchConfig))]
[MemoryDiagnoser]
public class SpecializedStringComparisonBenchmarks
{
    private class ComparisonBenchConfig : ManualConfig
    {
        public ComparisonBenchConfig()
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

    private const string LineFormat = "{0}: {1} units";

    private string _name = "order";
    private int _count = 42;

    [Benchmark(Baseline = true, Description = "Line | string.Format (BCL baseline)")]
    public string StringFormat_Line() => string.Format(LineFormat, _name, _count);

    [Benchmark(Description = "Line | StringBuilder direct (fresh per call)")]
    public string StringBuilderDirect_Line()
    {
        var sb = new StringBuilder();
        sb.Append(_name);
        sb.Append(": ");
        sb.Append(_count);
        sb.Append(" units");
        return sb.ToString();
    }

    [Benchmark(Description = "Line | StringBuilderPool (borrow + ToStringReturn)")]
    public string StringBuilderPool_Line()
    {
        var sb = StringBuilderPool.Instance.GetObject();
        sb.StringBuilder.Append(_name);
        sb.StringBuilder.Append(": ");
        sb.Append(_count);
        sb.StringBuilder.Append(" units");
        return sb.ToStringReturn();
    }

    [Benchmark(Description = "Line | StringBuilderPool.Format (generic helper)")]
    public string StringBuilderPoolFormat_Line()
        => StringBuilderPool.Format(LineFormat, _name, _count);

    [Benchmark(Description = "Line | HayateValueStringBuilder (Z1, zero-allocation)")]
    public string HayateValueStringBuilder_Line()
    {
        using var sb = new HayateValueStringBuilder();
        sb.Append(_name);
        sb.Append(": ");
        sb.Append(_count);
        sb.Append(" units");
        return sb.ToString();
    }

    /// <summary>
    /// Control row: the same build with a <c>void</c> return, isolating how the runner accounts the
    /// final string when it is not the benchmark's return value.
    /// </summary>
    [Benchmark(Description = "Line | HayateValueStringBuilder (void return control)")]
    public void HayateValueStringBuilder_VoidControl()
    {
        var sb = new HayateValueStringBuilder();
        sb.Append(_name);
        sb.Append(": ");
        sb.Append(_count);
        sb.Append(" units");
        _lastLine = sb.ToString();
        sb.Dispose();
    }

    private string? _lastLine;

    [Benchmark(Description = "Line | ZString value builder")]
    public string ZStringBuilder_Line()
    {
        using var sb = ZString.CreateStringBuilder();
        sb.Append(_name);
        sb.Append(": ");
        sb.Append(_count);
        sb.Append(" units");
        return sb.ToString();
    }

    [Benchmark(Description = "Line | ZString.Format")]
    public string ZStringFormat_Line() => ZString.Format(LineFormat, _name, _count);

    [Benchmark(Description = "Line | SharedStringBuilder (lock-guarded shared builder)")]
    public string SharedStringBuilder_Line()
        => SharedStringBuilder.Build(sb =>
        {
            sb.Append(_name);
            sb.Append(": ");
            sb.Append(_count.ToString());
            sb.Append(" units");
        });
}

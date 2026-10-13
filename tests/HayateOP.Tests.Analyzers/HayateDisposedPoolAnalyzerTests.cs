using System.Collections.Immutable;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class HayateDisposedPoolAnalyzerTests
{
    private static readonly HayateDisposedPoolAnalyzer Analyzer = new();

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => TestCompiler.AnalyzeAsync(source, Analyzer);

    [Fact]
    public async Task AcquireAfterDispose_IsWarning()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.Dispose();
        pool.Acquire();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO003", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.DisposedPoolUsed.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("pool.Acquire()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task AcquireBeforeDispose_IsNotReported()
    {
        // The only shape the rule can see is a dispose that precedes the borrow inside one body. A pool used
        // and then disposed is exactly what the API is for, so it must stay quiet.
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.Acquire();
        pool.Dispose();
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AcquireOnADifferentPool_IsNotReported()
    {
        // Disposal is per-symbol: disposing one pool says nothing about another. Comparing receivers by
        // display string rather than by symbol would fire here, and that is a false positive a user sees.
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> first, IHayateObjectPool<object> second)
    {
        first.Dispose();
        second.Acquire();
    }
}");

        Assert.Empty(diagnostics);
    }

    private static string ReportedText(Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        Assert.NotNull(tree);
        var span = diagnostic.Location.SourceSpan;
        return tree!.ToString().Substring(span.Start, span.Length);
    }
}

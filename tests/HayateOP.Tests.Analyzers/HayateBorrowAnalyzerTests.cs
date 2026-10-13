using System.Collections.Immutable;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class HayateBorrowAnalyzerTests
{
    private static readonly HayateBorrowAnalyzer Analyzer = new();

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => TestCompiler.AnalyzeAsync(source, Analyzer);

    // HYO009 — the lease and scope forms, Error level.

    [Fact]
    public async Task DiscardedScope_IsError()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.AcquireScoped();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO009", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.LeaseNotDisposed.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("pool.AcquireScoped()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task DiscardedLease_IsError()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.AcquireLeased();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO009", diagnostic.Id);
        Assert.Equal("pool.AcquireLeased()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task DiscardedLeaseInUnusedLocal_IsError()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        var lease = pool.AcquireLeased();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO009", diagnostic.Id);
        Assert.Equal("pool.AcquireLeased()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task ScopeInsideUsing_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        using (var scope = pool.AcquireScoped())
        {
        }
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ScopeDisposedInSameBody_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        var scope = pool.AcquireScoped();
        scope.Dispose();
    }
}");

        Assert.Empty(diagnostics);
    }

    // HYO001 — the plain object form, Warning level.

    [Fact]
    public async Task DiscardedPlainAcquire_IsWarning()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.Acquire();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO001", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.PooledObjectNotReleased.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("pool.Acquire()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task PlainAcquireStoredAndReleased_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        var item = pool.Acquire();
        pool.Release(item);
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task LeaseAndPlainAcquire_DiscriminateBySeverity()
    {
        // The rule that keys the split is the result type, so the same call shape must produce an Error for
        // the scope form and a Warning for the plain form. If the mapping ever collapses the two, the
        // rounded warning would let an unbalanced lease through a build that is otherwise green.
        var lease = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.AcquireScoped();
    }
}");
        var plain = await AnalyzeAsync(@"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.Acquire();
    }
}");

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(lease).Severity);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(plain).Severity);
    }

    private static string ReportedText(Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        Assert.NotNull(tree);
        var span = diagnostic.Location.SourceSpan;
        return tree!.ToString().Substring(span.Start, span.Length);
    }
}

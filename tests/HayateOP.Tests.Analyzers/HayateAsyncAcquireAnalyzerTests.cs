using System.Collections.Immutable;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class HayateAsyncAcquireAnalyzerTests
{
    private static readonly HayateAsyncAcquireAnalyzer Analyzer = new();

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => TestCompiler.AnalyzeAsync(source, Analyzer);

    // HYO004 — a blocking acquire inside an async method.

    [Fact]
    public async Task SynchronousAcquireInAsyncMethod_IsInfo()
    {
        var diagnostics = await AnalyzeAsync(@"
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool)
    {
        pool.Acquire();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO004", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.SynchronousAcquireInAsync.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("pool.Acquire()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task SynchronousAcquireInSyncMethod_IsNotReported()
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

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AwaitableAcquireInAsyncMethod_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(@"
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool)
    {
        await pool.AcquireAsync();
    }
}");

        Assert.Empty(diagnostics);
    }

    // HYO005 — an async acquire that drops the cancellation token in scope.

    [Fact]
    public async Task AsyncAcquireWithoutTokenWhileOneIsInScope_IsInfo()
    {
        var diagnostics = await AnalyzeAsync(@"
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool, CancellationToken cancellationToken)
    {
        await pool.AcquireAsync();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO005", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.CancellationTokenNotForwarded.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("pool.AcquireAsync()", ReportedText(diagnostic));
        Assert.Contains("cancellationToken", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AsyncAcquireWithTokenForwarded_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(@"
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool, CancellationToken cancellationToken)
    {
        await pool.AcquireAsync(cancellationToken);
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AsyncAcquireWithoutAnyTokenInScope_IsNotReported()
    {
        // There is nothing to forward, so the call is not an omission. Reporting it would be noise on the
        // single most common shape of an async borrow.
        var diagnostics = await AnalyzeAsync(@"
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool)
    {
        await pool.AcquireAsync();
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ExplicitCancellationTokenNone_CountsAsADecision()
    {
        // CancellationToken.None is written on purpose, not left out by accident. The rule keys on the
        // argument's type, so an explicit "none" is indistinguishable from a forward — and it must be
        // treated as one, otherwise the only way to silence the diagnostic is worse code.
        var diagnostics = await AnalyzeAsync(@"
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool, CancellationToken cancellationToken)
    {
        await pool.AcquireAsync(CancellationToken.None);
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

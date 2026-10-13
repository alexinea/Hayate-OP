using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class CodeFixTests
{
    // The three expected texts below were transcribed from a run that printed what the rewrites actually
    // produce, and are asserted as "the original with one segment replaced" — a whole-source comparison,
    // so an accidental change to trivia anywhere in the tree fails here.

    [Fact]
    public async Task WrapAcquireInUsing_ProducesAVerbatimUsing_AndFixesTheDiagnostic()
    {
        const string source = @"
using DotNetCore.HayateOP;

internal class Loaner
{
    internal void Take(IHayateObjectPool<object> pool)
    {
        pool.AcquireScoped();
    }
}";
        var root = TestCompiler.ParseRoot(source);
        var statement = root.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Single(s => s.Expression.ToString().StartsWith("pool.Acquire", System.StringComparison.Ordinal));

        var fixedSource = WrapAcquireInUsingCodeFix.Rewrite(root, statement).ToFullString();

        // Anchor 5: the fixed source is asserted verbatim.
        Assert.Equal(source.Replace("pool.AcquireScoped();", "using (pool.AcquireScoped()){}"), fixedSource);

        // …it compiles (otherwise "it must compile" in the anchor would rest on trust)…
        var compilation = TestCompiler.Compile(fixedSource);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));

        // …and re-analysing it reports nothing.
        Assert.Empty(await TestCompiler.AnalyzeAsync(fixedSource, new HayateBorrowAnalyzer()));
    }

    [Fact]
    public async Task UseAcquireAsync_ProducesAnAwaitedCall_AndFixesTheDiagnostic()
    {
        const string source = @"
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool)
    {
        pool.Acquire();
    }
}";
        var root = TestCompiler.ParseRoot(source);
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(s => s.ToString() == "pool.Acquire()");

        var fixedSource = UseAcquireAsyncCodeFix.Rewrite(root, invocation).ToFullString();

        Assert.Equal(source.Replace("pool.Acquire();", "await pool.AcquireAsync();"), fixedSource);

        var compilation = TestCompiler.Compile(fixedSource);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(await TestCompiler.AnalyzeAsync(fixedSource, new HayateAsyncAcquireAnalyzer()));
    }

    [Fact]
    public async Task ForwardCancellationToken_AppendsTheArgument_AndFixesTheDiagnostic()
    {
        const string source = @"
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

internal class Loaner
{
    internal async Task Take(IHayateObjectPool<object> pool, CancellationToken cancellationToken)
    {
        await pool.AcquireAsync();
    }
}";
        var root = TestCompiler.ParseRoot(source);
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(s => s.ToString() == "pool.AcquireAsync()");

        var fixedSource = ForwardCancellationTokenCodeFix.Rewrite(root, invocation, "cancellationToken").ToFullString();

        Assert.Equal(source.Replace("await pool.AcquireAsync();", "await pool.AcquireAsync(cancellationToken);"), fixedSource);

        var compilation = TestCompiler.Compile(fixedSource);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(await TestCompiler.AnalyzeAsync(fixedSource, new HayateAsyncAcquireAnalyzer()));
    }

    [Fact]
    public void EveryFix_DeclaresOnlyTheIdItFixes()
    {
        // A fix advertised against an id its analyzer never reports would be a dead menu item in the IDE,
        // and one advertised against the wrong id could rewrite code on the strength of a sibling rule.
        Assert.Equal("HYO009", Assert.Single(new WrapAcquireInUsingCodeFix().FixableDiagnosticIds));
        Assert.Equal("HYO004", Assert.Single(new UseAcquireAsyncCodeFix().FixableDiagnosticIds));
        Assert.Equal("HYO005", Assert.Single(new ForwardCancellationTokenCodeFix().FixableDiagnosticIds));
    }
}

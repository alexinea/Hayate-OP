using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class HayatePolicyAnalyzerTests
{
    private static readonly HayatePolicyAnalyzer Analyzer = new();

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => TestCompiler.AnalyzeAsync(source, Analyzer);

    [Fact]
    public async Task OnReleaseDisposesTheItem_IsWarning()
    {
        var diagnostics = await AnalyzeAsync(PolicyWith(
            @"
    public bool OnRelease(Item item)
    {
        item.Dispose();
        return true;
    }"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO006", diagnostic.Id);
        Assert.Equal(HayateDiagnostics.OnReleaseDisposesItem.DefaultSeverity, diagnostic.Severity);
        Assert.Equal("item.Dispose()", ReportedText(diagnostic));
    }

    [Fact]
    public async Task OnDestroyDisposesTheItem_IsNotReported()
    {
        // The same call in the hook that destroys is exactly right. Only OnRelease is a parking decision.
        var diagnostics = await AnalyzeAsync(PolicyWith(
            @"
    public bool OnRelease(Item item) => true;

    public void OnDestroy(Item item)
    {
        item.Dispose();
    }"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OnReleaseDisposesSomeOtherObject_IsNotReported()
    {
        // The rule is about the item the pool still owns. Tearing down an unrelated disposable inside a
        // policy is the caller's business, and a receiver matched by anything but the parameter symbol
        // would fire on it.
        var diagnostics = await AnalyzeAsync(PolicyWith(
            @"
    public bool OnRelease(Item item)
    {
        other.Dispose();
        return true;
    }"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OnReleaseThatOnlyReadsTheItem_IsNotReported()
    {
        var diagnostics = await AnalyzeAsync(PolicyWith(
            @"
    public bool OnRelease(Item item) => item.ToString() is not null;"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OnReleaseSuppressingFinalize_IsWarning()
    {
        // GC.SuppressFinalize on the item is the same claim as Dispose: that this object's teardown has
        // already happened. The default policy of the engine only resets, so a user writing this is doing
        // the teardown in the parking hook.
        var diagnostics = await AnalyzeAsync(PolicyWith(
            @"
    public bool OnRelease(Item item)
    {
        GC.SuppressFinalize(item);
        return true;
    }"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HYO006", diagnostic.Id);
    }

    private static string PolicyWith(string members)
    {
        // IHayateObjectPolicy<T> has six members, and a class that implements only some of them is
        // CS0535 — which leaves the analyzer with an error-recovered tree. The skeleton therefore
        // supplies OnDestroy unless the fixture brings its own, since one fixture is about OnDestroy
        // being the hook that is allowed to dispose.
        var onDestroy = members.Contains("OnDestroy", StringComparison.Ordinal)
            ? string.Empty
            : "\n\n    public void OnDestroy(Item item) { }";

        return @"
using System;
using DotNetCore.HayateOP.Policies;

internal sealed class Item : IDisposable
{
    public IDisposable Other => new Item();

    public void Dispose() { }
}

internal sealed class ItemPolicy : IHayateObjectPolicy<Item>
{
    private readonly IDisposable other = new Item();
" + members + onDestroy + @"

    public Item Create() => new Item();

    public bool Validate(Item item) => true;

    public void OnAcquire(Item item) { }

    public void OnPassivate(Item item) { }
}
";
    }

    private static string ReportedText(Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        Assert.NotNull(tree);
        var span = diagnostic.Location.SourceSpan;
        return tree!.ToString().Substring(span.Start, span.Length);
    }
}

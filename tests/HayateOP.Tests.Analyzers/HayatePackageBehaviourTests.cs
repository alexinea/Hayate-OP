using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Analyzers;

public class HayatePackageBehaviourTests
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
        new HayateBorrowAnalyzer(),
        new HayateAsyncAcquireAnalyzer(),
        new HayateDisposedPoolAnalyzer(),
        new HayatePolicyAnalyzer());

    [Fact]
    public async Task LookAlikeAcquireOutsideTheNamespace_IsNotReported()
    {
        // Acceptance anchor 3. The rules key on the containing namespace, not on the method name, so a
        // user's own Acquire must be invisible to them however identical the call site looks.
        var diagnostics = await RunAllAsync(@"
namespace Sample
{

internal sealed class LookAlikePool
{
    internal object Acquire() => new object();

    internal void Dispose() { }
}

internal sealed class Loaner
{
    internal void Take(LookAlikePool pool)
    {
        pool.Acquire();
        pool.Dispose();
    }
}
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CompilationWithoutHayateOP_ProducesNothing()
    {
        // Acceptance anchor 4. The package is inert for a project that does not use the library; a single
        // stray diagnostic here would mean a rule is anchoring on something other than the library's own
        // types.
        var diagnostics = await RunAllAsync(@"
namespace Sample
{

internal sealed class Loaner
{
    internal int Take()
    {
        using var disposable = new System.IO.MemoryStream();
        return disposable.Length.GetHashCode();
    }
}
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShippedSeverities_AreTheIntendedOnes()
    {
        // Anchor 2 read back off the descriptors rather than restated here. HYO009 is the one that carries
        // the decision: a dropped lease is permanent, so it is the Error.
        Assert.Equal(DiagnosticSeverity.Error, HayateDiagnostics.LeaseNotDisposed.DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Warning, HayateDiagnostics.PooledObjectNotReleased.DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Warning, HayateDiagnostics.DisposedPoolUsed.DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Warning, HayateDiagnostics.OnReleaseDisposesItem.DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Info, HayateDiagnostics.SynchronousAcquireInAsync.DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Info, HayateDiagnostics.CancellationTokenNotForwarded.DefaultSeverity);
    }

    [Fact]
    public void EveryDescriptor_IsListedInTheReleaseTrackingFiles()
    {
        // The other half of RS2008: the release-tracking files are what keep a descriptor from shipping
        // without a row in one of them. This parses the real files and diffs the ids, so adding a
        // descriptor without its row fails here instead of relying on the compiler's warning surviving
        // the gate. Both files are read rather than Unshipped alone: a rule moves from Unshipped to
        // Shipped at the release that first carries it, and the invariant has to hold on either side of
        // that move -- reading one file would call the release itself a regression.
        var unshipped = TrackedIds("AnalyzerReleases.Unshipped.md");
        var shipped = TrackedIds("AnalyzerReleases.Shipped.md");
        var tracked = unshipped.Concat(shipped).ToHashSet(StringComparer.Ordinal);
        var descriptors = new[]
        {
            HayateDiagnostics.LeaseNotDisposed,
            HayateDiagnostics.PooledObjectNotReleased,
            HayateDiagnostics.DisposedPoolUsed,
            HayateDiagnostics.SynchronousAcquireInAsync,
            HayateDiagnostics.CancellationTokenNotForwarded,
            HayateDiagnostics.OnReleaseDisposesItem,
        }.Select(d => d.Id).ToHashSet();

        Assert.Equal(6, tracked.Count);
        Assert.Equal(descriptors.OrderBy(id => id, StringComparer.Ordinal), tracked.OrderBy(id => id, StringComparer.Ordinal));
        // A move, not a copy: an id present in both files means the row was duplicated rather than
        // relocated, which is the one way this file pair can be wrong while each half looks right.
        Assert.Empty(unshipped.Intersect(shipped, StringComparer.Ordinal));
    }

    private static string[] TrackedIds(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
            "HayateOP.Extensions.Analyzers", fileName);
        Assert.True(File.Exists(path), $"release tracking not found at {path}");
        return File.ReadAllLines(path)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length > 1 && parts[0].Trim().StartsWith("HYO", StringComparison.Ordinal))
            .Select(parts => parts[0].Trim())
            .ToArray();
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAllAsync(string source)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var analyzer in Analyzers)
        {
            diagnostics.AddRange(await TestCompiler.AnalyzeAsync(source, analyzer).ConfigureAwait(false));
        }

        return diagnostics.ToImmutable();
    }
}

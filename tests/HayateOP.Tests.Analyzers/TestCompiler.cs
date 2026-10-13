using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetCore.HayateOP;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetCore.HayateOP.Tests.Analyzers;

/// <summary>
/// Compiles a snippet in memory against the real HayateOP assembly and runs one analyzer over it.
/// </summary>
/// <remarks>
/// The rules decide from symbols, so the test must give them real symbols: a snippet compiled against a
/// hand-written stub would exercise the syntax half of each rule and silently skip the namespace gate
/// that keeps the rules off a user type with an identically named <c>Acquire</c>.
/// </remarks>
internal static class TestCompiler
{
    private static readonly ImmutableArray<MetadataReference> FrameworkReferences = LoadFrameworkReferences();

    private static readonly MetadataReference HayateReference =
        MetadataReference.CreateFromFile(typeof(IHayateObjectPool).Assembly.Location);

    /// <summary>Parses a snippet without binding it.</summary>
    /// <param name="source">The C# source.</param>
    /// <returns>The syntax root.</returns>
    internal static SyntaxNode ParseRoot(string source)
        => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();

    /// <summary>Compiles a snippet against the framework and the real HayateOP assembly.</summary>
    /// <param name="source">The C# source.</param>
    /// <returns>The compilation, bound but not emitted.</returns>
    internal static CSharpCompilation Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        return CSharpCompilation.Create(
            "HayateAnalyzerTest",
            new[] { tree },
            FrameworkReferences.Add(HayateReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs one analyzer over a snippet and returns what it reported.</summary>
    /// <param name="source">The C# source.</param>
    /// <param name="analyzer">The analyzer under test.</param>
    /// <returns>The analyzer diagnostics, in the order the host produced them.</returns>
    internal static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var compilation = Compile(source);

        // A snippet that does not bind still runs through the analyzer — Roslyn recovers and hands it a
        // tree with placeholder nodes — so every diagnostic it produces would come from a shape the
        // compiler does not agree exists. Asserting this here gives all of the fixtures that property
        // from one place; the first version of these fixtures shipped file-scoped namespaces, which this
        // is what caught (they passed either way, which is exactly why the assertion is needed).
        var broken = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (broken.Length > 0)
        {
            var detail = string.Join("\n", broken.Select(d => $"  {d.Id}: {d.GetMessage()}"));
            throw new InvalidOperationException($"fixture does not compile:\n{detail}");
        }

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
    }

    private static ImmutableArray<MetadataReference> LoadFrameworkReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
        {
            return builder.ToImmutable();
        }

        foreach (var path in trusted.Split(Path.PathSeparator))
        {
            if (path.Length > 0 && File.Exists(path))
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return builder.ToImmutable();
    }
}

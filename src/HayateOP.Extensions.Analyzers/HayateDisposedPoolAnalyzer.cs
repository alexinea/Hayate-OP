using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// Reports <c>HYO003</c>: a pool that was disposed earlier in the same method is used to acquire again.
/// </summary>
/// <remarks>
/// Dispose is terminal. For <c>ParameterizedHayatePool</c> and <c>HayateSharedPoolRegistry</c> a later
/// borrow throws <c>ObjectDisposedException</c>; for the ordinary <c>HayateObjectPool&lt;T&gt;</c> it does
/// not throw at all — it operates on a pool whose shards, timers and registry entry are already gone.
/// Both are misuse; only the first announces itself, which is what makes the silent one worth a
/// diagnostic.
/// <para>
/// The check is straight-line and within one method body: it follows explicit <c>Dispose()</c> /
/// <c>DisposeAsync()</c> calls on the same symbol. See <c>docs/analyzers.md</c> section 3.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HayateDisposedPoolAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(HayateDiagnostics.DisposedPoolUsed);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        try
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (!HayateSymbolFacts.IsHayateAcquire(method))
            {
                return;
            }

            var receiver = HayateSymbolFacts.ReceiverSymbol(context.SemanticModel, invocation);
            if (receiver is null)
            {
                return;
            }

            var function = HayateSymbolFacts.EnclosingFunction(invocation);
            foreach (var earlier in PrecedingStatements(invocation))
            {
                foreach (var candidate in earlier.DescendantNodesAndSelf())
                {
                    if (candidate is not InvocationExpressionSyntax disposeInvocation)
                    {
                        continue;
                    }

                    // A dispose inside a nested lambda or local function is not a dispose in this body.
                    if (!ReferenceEquals(HayateSymbolFacts.EnclosingFunction(disposeInvocation), function))
                    {
                        continue;
                    }

                    var disposeMethod = context.SemanticModel.GetSymbolInfo(disposeInvocation).Symbol as IMethodSymbol;
                    if (!HayateSymbolFacts.IsDisposeCall(disposeMethod))
                    {
                        continue;
                    }

                    var disposedReceiver = HayateSymbolFacts.ReceiverSymbol(context.SemanticModel, disposeInvocation);
                    if (SymbolEqualityComparer.Default.Equals(disposedReceiver, receiver))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            HayateDiagnostics.DisposedPoolUsed, invocation.GetLocation(), receiver.Name));
                        return;
                    }
                }
            }
        }
        catch (Exception)
        {
            // See HayateBorrowAnalyzer: never let an unexpected shape become AD0001 in the user's build.
        }
    }

    /// <summary>
    /// Yields the statements that precede <paramref name="node"/> within the enclosing blocks, nearest
    /// block first.
    /// </summary>
    /// <param name="node">The node whose preceding statements are wanted.</param>
    /// <returns>The preceding statements, innermost block first.</returns>
    private static IEnumerable<StatementSyntax> PrecedingStatements(SyntaxNode node)
    {
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            if (current.Parent is BlockSyntax block)
            {
                foreach (var statement in block.Statements)
                {
                    if (statement.SpanStart >= node.SpanStart)
                    {
                        break;
                    }

                    yield return statement;
                }
            }
            else if (current.Parent is SwitchSectionSyntax section)
            {
                foreach (var statement in section.Statements)
                {
                    if (statement.SpanStart >= node.SpanStart)
                    {
                        break;
                    }

                    yield return statement;
                }
            }
        }
    }
}

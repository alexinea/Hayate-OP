using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// Reports the two asynchronous-path advisories: <c>HYO004</c> (a blocking acquire inside an async
/// method) and <c>HYO005</c> (an async acquire that does not forward a cancellation token already in
/// scope).
/// </summary>
/// <remarks>
/// Both are <see cref="DiagnosticSeverity.Info"/> on purpose: a borrow released before the first
/// <c>await</c> is legitimate, and the analyzer cannot see whether a borrow crosses one. The rules point
/// at the shape that yields, they do not assert a defect. See <c>docs/analyzers.md</c> section 3.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HayateAsyncAcquireAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            HayateDiagnostics.SynchronousAcquireInAsync,
            HayateDiagnostics.CancellationTokenNotForwarded);

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

            var enclosing = HayateSymbolFacts.EnclosingMethod(context.SemanticModel, invocation);
            if (enclosing is null)
            {
                return;
            }

            if (HayateSymbolFacts.IsSynchronousAcquire(method!.Name))
            {
                if (enclosing.IsAsync)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        HayateDiagnostics.SynchronousAcquireInAsync, invocation.GetLocation(), method.Name));
                }

                return;
            }

            if (!HayateSymbolFacts.IsAsyncAcquire(method.Name))
            {
                return;
            }

            // An explicit token — including CancellationToken.None — is a decision, not an omission.
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (HayateSymbolFacts.IsCancellationToken(context.SemanticModel.GetTypeInfo(argument.Expression).Type))
                {
                    return;
                }
            }

            var token = HayateSymbolFacts.FindCancellationTokenParameter(enclosing);
            if (token is null)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HayateDiagnostics.CancellationTokenNotForwarded, invocation.GetLocation(), method.Name, token.Name));
        }
        catch (Exception)
        {
            // See HayateBorrowAnalyzer: never let an unexpected shape become AD0001 in the user's build.
        }
    }
}

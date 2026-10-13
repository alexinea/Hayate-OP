using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// Reports a borrow whose result is thrown away: <c>HYO009</c> for the lease and scope forms (Error) and
/// <c>HYO001</c> for the plain object form (Warning).
/// </summary>
/// <remarks>
/// Two shapes are reported, both decided by syntax rather than by control flow: the acquire invocation is
/// the whole of an expression statement, or it initialises a local that is never referenced again.
/// Anything else counts as handled. See <c>docs/analyzers.md</c> sections 2.1 and 2.2.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HayateBorrowAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(HayateDiagnostics.PooledObjectNotReleased, HayateDiagnostics.LeaseNotDisposed);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeExpressionStatement, SyntaxKind.ExpressionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeVariableDeclarator, SyntaxKind.VariableDeclarator);
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        try
        {
            var statement = (ExpressionStatementSyntax)context.Node;
            if (statement.Expression is not InvocationExpressionSyntax invocation)
            {
                return;
            }

            var method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (!HayateSymbolFacts.IsHayateAcquire(method))
            {
                return;
            }

            if (HayateSymbolFacts.IsInsideUsing(invocation))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DescriptorFor(method!.Name), invocation.GetLocation(), method.Name));
        }
        catch (Exception)
        {
            // An analyzer that throws is reported by the host as AD0001, which in a zero-warning build is
            // a broken build for the consumer. Stay silent on an unexpected shape instead.
        }
    }

    private static void AnalyzeVariableDeclarator(SyntaxNodeAnalysisContext context)
    {
        try
        {
            var declarator = (VariableDeclaratorSyntax)context.Node;
            if (declarator.Initializer?.Value is not InvocationExpressionSyntax invocation)
            {
                return;
            }

            var method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (!HayateSymbolFacts.IsHayateAcquire(method))
            {
                return;
            }

            if (HayateSymbolFacts.IsInsideUsing(declarator))
            {
                return;
            }

            // Only a local: a field is out of this rule's reach by design (it outlives the method).
            if (context.SemanticModel.GetDeclaredSymbol(declarator) is not ILocalSymbol local)
            {
                return;
            }

            var scope = HayateSymbolFacts.EnclosingFunction(declarator) ?? (SyntaxNode)declarator.SyntaxTree.GetRoot();
            if (HayateSymbolFacts.HasOtherReference(context.SemanticModel, local, scope))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DescriptorFor(method!.Name), invocation.GetLocation(), method.Name));
        }
        catch (Exception)
        {
            // See AnalyzeExpressionStatement.
        }
    }

    private static DiagnosticDescriptor DescriptorFor(string name)
        => HayateSymbolFacts.IsScopedAcquire(name) || HayateSymbolFacts.IsLeasedAcquire(name)
            ? HayateDiagnostics.LeaseNotDisposed
            : HayateDiagnostics.PooledObjectNotReleased;
}

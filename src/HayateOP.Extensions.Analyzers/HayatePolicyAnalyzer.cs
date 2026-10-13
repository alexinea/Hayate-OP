using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// Reports <c>HYO006</c>: an <c>IHayateObjectPolicy&lt;T&gt;.OnRelease</c> implementation disposes the item
/// instead of releasing it.
/// </summary>
/// <remarks>
/// <c>OnRelease</c> answers whether the object may be parked; disposing the item there destroys an object
/// the pool still owns and will hand to the next borrower. Teardown belongs in <c>OnDestroy</c>, which the
/// pool calls when it destroys an object for good. The rule fires on <c>Dispose</c>, <c>DisposeAsync</c>
/// and <c>GC.SuppressFinalize</c> reached from the item parameter.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HayatePolicyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(HayateDiagnostics.OnReleaseDisposesItem);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        try
        {
            var declaration = (MethodDeclarationSyntax)context.Node;
            if (context.SemanticModel.GetDeclaredSymbol(declaration) is not IMethodSymbol method
                || !HayateSymbolFacts.IsOnReleaseImplementation(method))
            {
                return;
            }

            if (declaration.ParameterList.Parameters.Count != 1)
            {
                return;
            }

            var item = context.SemanticModel.GetDeclaredSymbol(declaration.ParameterList.Parameters[0]);
            if (item is null)
            {
                return;
            }

            foreach (var node in declaration.DescendantNodes())
            {
                if (node is not InvocationExpressionSyntax invocation)
                {
                    continue;
                }

                var invoked = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (invoked is null)
                {
                    continue;
                }

                if (!HayateSymbolFacts.IsDisposeCall(invoked) && !IsSuppressFinalize(invoked))
                {
                    continue;
                }

                if (!IsRootedAtItem(context.SemanticModel, invocation, item))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    HayateDiagnostics.OnReleaseDisposesItem, invocation.GetLocation(), method.Name));
            }
        }
        catch (Exception)
        {
            // See HayateBorrowAnalyzer: never let an unexpected shape become AD0001 in the user's build.
        }
    }

    private static bool IsSuppressFinalize(IMethodSymbol method)
        => method.Name == "SuppressFinalize"
           && method.ContainingType?.ToDisplayString() == "System.GC";

    /// <summary>
    /// Whether the call reaches the item parameter: either as the receiver of a member access, or as an
    /// argument of <c>GC.SuppressFinalize(item)</c>, which passes the item rather than calling it.
    /// </summary>
    /// <param name="model">The semantic model.</param>
    /// <param name="invocation">The call.</param>
    /// <param name="item">The item parameter.</param>
    /// <returns><see langword="true"/> when the call is rooted at <paramref name="item"/>.</returns>
    private static bool IsRootedAtItem(SemanticModel model, InvocationExpressionSyntax invocation, ISymbol item)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        if (IsRootedAt(model, memberAccess.Expression, item))
        {
            return true;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is IdentifierNameSyntax identifier
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an expression is the item parameter itself or a member reached from it.
    /// </summary>
    /// <param name="model">The semantic model.</param>
    /// <param name="expression">The expression to test.</param>
    /// <param name="item">The item parameter.</param>
    /// <returns><see langword="true"/> when the expression is rooted at <paramref name="item"/>.</returns>
    private static bool IsRootedAt(SemanticModel model, ExpressionSyntax expression, ISymbol item)
    {
        var current = expression;
        while (current is MemberAccessExpressionSyntax inner)
        {
            current = inner.Expression;
        }

        if (current is IdentifierNameSyntax identifier)
        {
            return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, item);
        }

        return false;
    }
}

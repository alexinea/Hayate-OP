using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// The fix for <c>HYO004</c>: replaces a blocking acquire with its awaitable counterpart.
/// </summary>
/// <remarks>
/// The rename is a fixed map — <c>Acquire</c> to <c>AcquireAsync</c>, <c>AcquireScoped</c> to
/// <c>AcquireScopeAsync</c>, <c>AcquireLeased</c> to <c>AcquireLeaseAsync</c> — and the call is wrapped in
/// <c>await</c>. The rule only fires inside an <c>async</c> method, so the <c>await</c> is always legal
/// where the fix is offered. The existing argument list is kept, which is what makes the overload
/// resolution land on the awaited form.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseAcquireAsyncCodeFix))]
[Shared]
public sealed class UseAcquireAsyncCodeFix : CodeFixProvider
{
    private const string Title = "Use the awaitable acquire";

    private const string EquivalenceKey = "HYO004.UseAcquireAsync";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("HYO004");

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            if (AsyncName(invocation) is null)
            {
                continue;
            }

            var newRoot = Rewrite(root, invocation);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    EquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>
    /// Rewrites a blocking acquire invocation into its awaited counterpart.
    /// </summary>
    /// <param name="root">The syntax root.</param>
    /// <param name="invocation">The blocking acquire invocation.</param>
    /// <returns>The rewritten root.</returns>
    internal static SyntaxNode Rewrite(SyntaxNode root, InvocationExpressionSyntax invocation)
    {
        if (AsyncName(invocation) is not string asyncName
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return root;
        }

        var leading = invocation.GetLeadingTrivia();
        var trailing = invocation.GetTrailingTrivia();

        var renamed = invocation
            .WithExpression(memberAccess.WithName(SyntaxFactory.IdentifierName(asyncName)))
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia();

        var awaited = SyntaxFactory
            .AwaitExpression(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithLeadingTrivia(leading).WithTrailingTrivia(SyntaxFactory.Space),
                renamed)
            .WithTrailingTrivia(trailing);

        return root.ReplaceNode(invocation, awaited);
    }

    private static string? AsyncName(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        return memberAccess.Name.Identifier.ValueText switch
        {
            "Acquire" => "AcquireAsync",
            "AcquireScoped" => "AcquireScopeAsync",
            "AcquireLeased" => "AcquireLeaseAsync",
            _ => null,
        };
    }
}

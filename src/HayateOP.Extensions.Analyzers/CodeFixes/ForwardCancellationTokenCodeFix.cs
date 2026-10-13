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
/// The fix for <c>HYO005</c>: appends the enclosing method's cancellation token to an async acquire that
/// did not forward it.
/// </summary>
/// <remarks>
/// The rule only fires when a token is already in scope, so the fix never invents one: it appends the
/// parameter that is there. Appending is also the correct position — every async acquire overload takes
/// its <c>CancellationToken</c> last, whether the call passes a timeout or nothing at all.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ForwardCancellationTokenCodeFix))]
[Shared]
public sealed class ForwardCancellationTokenCodeFix : CodeFixProvider
{
    private const string Title = "Forward the cancellation token";

    private const string EquivalenceKey = "HYO005.ForwardCancellationToken";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("HYO005");

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

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model is null)
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

            var token = HayateSymbolFacts.FindCancellationTokenParameter(
                HayateSymbolFacts.EnclosingMethod(model, invocation));
            if (token is null)
            {
                continue;
            }

            var newRoot = Rewrite(root, invocation, token.Name);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    EquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>
    /// Appends a cancellation token argument to an invocation.
    /// </summary>
    /// <param name="root">The syntax root.</param>
    /// <param name="invocation">The acquire invocation.</param>
    /// <param name="tokenName">The name of the token to forward.</param>
    /// <returns>The rewritten root.</returns>
    internal static SyntaxNode Rewrite(SyntaxNode root, InvocationExpressionSyntax invocation, string tokenName)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tokenName));
        var updated = invocation.WithArgumentList(invocation.ArgumentList.AddArguments(argument));

        return root.ReplaceNode(invocation, updated);
    }
}

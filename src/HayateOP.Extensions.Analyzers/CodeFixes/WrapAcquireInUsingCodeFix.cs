using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// The fix for <c>HYO009</c>: wraps a discarded acquire in a <c>using</c> statement, so the lease is
/// returned instead of lost.
/// </summary>
/// <remarks>
/// Only the discarded shape is fixed. The acquire still happens and the block is empty: the fix turns a
/// permanent leak into an immediate return and leaves a scaffold for the caller's work. It does not move
/// any code into the block, because guessing what belonged there would be a silent semantic change.
/// <para>
/// A <c>using</c> statement is emitted rather than a <c>using</c> declaration (<c>using var</c>), so the
/// result compiles for a consumer on any C# version the compiler accepts. The scope and lease types
/// implement <c>IDisposable</c> on every target framework, so the synchronous form is correct even when
/// the acquire itself is awaited.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WrapAcquireInUsingCodeFix))]
[Shared]
public sealed class WrapAcquireInUsingCodeFix : CodeFixProvider
{
    private const string Title = "Wrap in a using statement";

    private const string EquivalenceKey = "HYO009.WrapInUsing";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("HYO009");

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

            // Only the discarded shape has a fix: an unused local would need a placement decision.
            var statement = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
            if (statement is null || !statement.Expression.Span.Contains(diagnostic.Location.SourceSpan))
            {
                continue;
            }

            var newRoot = Rewrite(root, statement);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    EquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>
    /// Rewrites a discarded acquire statement into a <c>using</c> statement.
    /// </summary>
    /// <param name="root">The syntax root.</param>
    /// <param name="statement">The statement whose expression is the discarded acquire.</param>
    /// <returns>The rewritten root.</returns>
    internal static SyntaxNode Rewrite(SyntaxNode root, ExpressionStatementSyntax statement)
    {
        // Roslyn 3.8's UsingStatementSyntax carries AttributeLists and AwaitKeyword, so the only factory
        // that produces `using (expr) stmt` is the eight-argument one; the shorter overloads arrived later.
        // `declaration` is null because the statement form being built is the expression form.
        var usingStatement = SyntaxFactory
            .UsingStatement(
                attributeLists: default,
                awaitKeyword: default,
                usingKeyword: SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                openParenToken: SyntaxFactory.Token(SyntaxKind.OpenParenToken),
                declaration: null!,
                expression: statement.Expression.WithoutTrivia(),
                closeParenToken: SyntaxFactory.Token(SyntaxKind.CloseParenToken),
                statement: SyntaxFactory.Block())
            .WithTriviaFrom(statement)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return root.ReplaceNode(statement, usingStatement);
    }
}

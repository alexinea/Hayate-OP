using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetCore.HayateOP.Analyzers;

/// <summary>
/// The facts every rule in this package agrees on: which calls are pool calls, which are acquires, and
/// how to find the function a node lives in.
/// </summary>
/// <remarks>
/// Every decision here is made from symbols, never from a display string that a <c>using</c> alias could
/// change, and every entry point tolerates <see langword="null"/> so an unexpected shape degrades to
/// "no diagnostic" rather than to an exception inside the user's build.
/// </remarks>
internal static class HayateSymbolFacts
{
    /// <summary>The library's root namespace; a call is a pool call only inside it.</summary>
    internal const string RootNamespace = "DotNetCore.HayateOP";

    /// <summary>Whether <paramref name="ns"/> is the library's root namespace or below it.</summary>
    /// <param name="ns">The namespace to test.</param>
    /// <returns><see langword="true"/> when the namespace is the library's or nested in it.</returns>
    internal static bool IsHayateNamespace(INamespaceSymbol? ns)
    {
        var name = ns?.ToDisplayString();
        if (name is null)
        {
            return false;
        }

        return name == RootNamespace
            || name.StartsWith(RootNamespace + ".", StringComparison.Ordinal);
    }

    /// <summary>Whether a method name is any acquire on a pool.</summary>
    /// <param name="name">The method name.</param>
    /// <returns><see langword="true"/> for the scoped, leased and plain acquire names.</returns>
    internal static bool IsAcquireName(string name) => name switch
    {
        "Acquire" => true,
        "AcquireAsync" => true,
        "AcquireScoped" => true,
        "AcquireScopeAsync" => true,
        "AcquireLeased" => true,
        "AcquireLeaseAsync" => true,
        _ => false,
    };

    /// <summary>Whether an acquire returns a <c>HayatePoolScope&lt;T&gt;</c>.</summary>
    /// <param name="name">The method name.</param>
    /// <returns><see langword="true"/> for the scoped names.</returns>
    internal static bool IsScopedAcquire(string name) => name == "AcquireScoped" || name == "AcquireScopeAsync";

    /// <summary>Whether an acquire returns a <c>HayatePoolLease&lt;T&gt;</c>.</summary>
    /// <param name="name">The method name.</param>
    /// <returns><see langword="true"/> for the leased names.</returns>
    internal static bool IsLeasedAcquire(string name) => name == "AcquireLeased" || name == "AcquireLeaseAsync";

    /// <summary>Whether an acquire is blocking.</summary>
    /// <param name="name">The method name.</param>
    /// <returns><see langword="true"/> when the call blocks the calling thread.</returns>
    internal static bool IsSynchronousAcquire(string name)
        => name == "Acquire" || name == "AcquireScoped" || name == "AcquireLeased";

    /// <summary>Whether an acquire is awaitable.</summary>
    /// <param name="name">The method name.</param>
    /// <returns><see langword="true"/> for the async acquire names.</returns>
    internal static bool IsAsyncAcquire(string name)
        => name == "AcquireAsync" || name == "AcquireScopeAsync" || name == "AcquireLeaseAsync";

    /// <summary>
    /// Whether a method symbol is an acquire on a HayateOP pool.
    /// </summary>
    /// <param name="method">The resolved method, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the name matches and the declaring namespace is the library's.</returns>
    /// <remarks>
    /// The namespace gate is what keeps the rules off a user type that happens to have an <c>Acquire</c>
    /// method. It also covers the scope and lease extension methods, which is why the test is on the
    /// method's own containing type rather than on the receiver.
    /// </remarks>
    internal static bool IsHayateAcquire(IMethodSymbol? method)
        => method is not null
           && IsAcquireName(method.Name)
           && IsHayateNamespace(method.ContainingNamespace);

    /// <summary>Whether a method symbol is <c>Dispose</c> or <c>DisposeAsync</c>.</summary>
    /// <param name="method">The resolved method, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for the two disposal methods.</returns>
    internal static bool IsDisposeCall(IMethodSymbol? method)
        => method is not null && (method.Name == "Dispose" || method.Name == "DisposeAsync");

    /// <summary>
    /// Whether a method implements <c>IHayateObjectPolicy&lt;T&gt;.OnRelease</c>.
    /// </summary>
    /// <param name="method">The resolved method, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the method is an <c>OnRelease</c> hook of that interface.</returns>
    internal static bool IsOnReleaseImplementation(IMethodSymbol? method)
    {
        if (method is null || method.Name != "OnRelease" || method.Parameters.Length != 1)
        {
            return false;
        }

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            if (iface.Name == "IHayateObjectPolicy"
                && iface.TypeArguments.Length == 1
                && IsHayateNamespace(iface.ContainingNamespace))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a symbol is a <c>CancellationToken</c>.</summary>
    /// <param name="type">The type to test.</param>
    /// <returns><see langword="true"/> for <c>System.Threading.CancellationToken</c>.</returns>
    internal static bool IsCancellationToken(ITypeSymbol? type)
        => type is not null
           && type.Name == "CancellationToken"
           && type.ContainingNamespace?.ToDisplayString() == "System.Threading";

    /// <summary>
    /// The first <c>CancellationToken</c> parameter of a method, or <see langword="null"/>.
    /// </summary>
    /// <param name="method">The method, or <see langword="null"/>.</param>
    /// <returns>The token parameter when the method has one.</returns>
    internal static IParameterSymbol? FindCancellationTokenParameter(IMethodSymbol? method)
    {
        if (method is null)
        {
            return null;
        }

        foreach (var parameter in method.Parameters)
        {
            if (IsCancellationToken(parameter.Type))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol of the receiver of a member access, or <see langword="null"/>.
    /// </summary>
    /// <param name="model">The semantic model.</param>
    /// <param name="invocation">The invocation.</param>
    /// <returns>The receiver symbol when the call is <c>receiver.Method(...)</c>; otherwise <see langword="null"/>.</returns>
    internal static ISymbol? ReceiverSymbol(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return model.GetSymbolInfo(memberAccess.Expression).Symbol;
        }

        return null;
    }

    /// <summary>
    /// The nearest enclosing function-like node: a method, constructor, accessor, local function, lambda
    /// or anonymous method.
    /// </summary>
    /// <param name="node">The node to start from.</param>
    /// <returns>The enclosing function-like node, or <see langword="null"/> at the top level.</returns>
    internal static SyntaxNode? EnclosingFunction(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol of the function a node lives in.
    /// </summary>
    /// <param name="model">The semantic model.</param>
    /// <param name="node">The node to start from.</param>
    /// <returns>The enclosing method symbol, or <see langword="null"/> when there is none.</returns>
    internal static IMethodSymbol? EnclosingMethod(SemanticModel model, SyntaxNode node)
    {
        var function = EnclosingFunction(node);
        if (function is null)
        {
            return null;
        }

        return function switch
        {
            AnonymousFunctionExpressionSyntax anonymous => model.GetSymbolInfo(anonymous).Symbol as IMethodSymbol,
            _ => model.GetDeclaredSymbol(function) as IMethodSymbol,
        };
    }

    /// <summary>
    /// Whether a node sits inside a <c>using</c> statement or a <c>using</c> declaration, i.e. its value
    /// is already handed to the runtime for disposal.
    /// </summary>
    /// <param name="node">The node to test.</param>
    /// <returns><see langword="true"/> when the node is part of a using construct.</returns>
    internal static bool IsInsideUsing(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is UsingStatementSyntax)
            {
                return true;
            }

            if (current is LocalDeclarationStatementSyntax local && local.UsingKeyword != default)
            {
                return true;
            }

            // Stop at the function boundary: a using in an outer scope does not cover this node.
            if (current is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a local is referenced anywhere in <paramref name="scope"/> other than at its declaration.
    /// </summary>
    /// <param name="model">The semantic model.</param>
    /// <param name="local">The local symbol.</param>
    /// <param name="scope">The node to search.</param>
    /// <returns><see langword="true"/> when the local has at least one other reference.</returns>
    /// <remarks>
    /// A reference count, not a control-flow analysis: a local disposed on one branch and leaked on the
    /// other counts as referenced. That is deliberate — the rule only reports a local that is written and
    /// then never mentioned again, which is unambiguous. See <c>docs/analyzers.md</c> section 3.
    /// </remarks>
    internal static bool HasOtherReference(SemanticModel model, ILocalSymbol local, SyntaxNode scope)
    {
        foreach (var node in scope.DescendantNodes())
        {
            if (node is IdentifierNameSyntax identifier)
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (SymbolEqualityComparer.Default.Equals(symbol, local))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a syntax token list contains the <c>async</c> modifier.</summary>
    /// <param name="modifiers">The token list.</param>
    /// <returns><see langword="true"/> when <c>async</c> is present.</returns>
    internal static bool IsAsync(SyntaxTokenList modifiers)
    {
        foreach (var token in modifiers)
        {
            if (token.IsKind(SyntaxKind.AsyncKeyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An empty diagnostic array, returned when a rule declines to report.</summary>
    internal static ImmutableArray<Diagnostic> None => ImmutableArray<Diagnostic>.Empty;
}

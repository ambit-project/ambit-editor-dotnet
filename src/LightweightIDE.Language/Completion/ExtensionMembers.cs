using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LightweightIDE.Language.Completion;

/// <summary>
/// Members of C# 14 extension blocks (<c>extension(Order order) { public decimal Gross => …; public Order With() … }</c>)
/// available after the "." at a position. Roslyn lists them like ordinary properties and classic extension methods;
/// the popup marks them "extension · OrderExtensions".
/// </summary>
static class ExtensionMembers
{
    /// <summary>
    /// An unimported item that is a member of an extension block: its symbol key names the block's generated
    /// "&lt;M&gt;$…" marker type (classic extension methods have none).
    /// </summary>
    public static bool IsBlockMember(CompletionItem item) =>
        item.Properties.TryGetValue("MethodKey", out var key) && key.Contains("\"<M>$", StringComparison.Ordinal);

    /// <returns>Property or method name → the static class that declares the extension block.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> FindAsync(Document document, int position, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not { } root) return result;
        if (Receiver(root, position) is not { } receiver) return result;
        if (await document.GetSemanticModelAsync(ct).ConfigureAwait(false) is not { } model) return result;

        // `Order.` (a type: static members) or `order.` (a value: instance members).
        var symbol = model.GetSymbolInfo(receiver, ct).Symbol;
        var isType = symbol is ITypeSymbol;
        var type = isType ? (ITypeSymbol)symbol! : model.GetTypeInfo(receiver, ct).Type;
        if (type is null or IErrorTypeSymbol) return result;

        // LookupStaticMembers leaves out static extension members; LookupSymbols returns both kinds.
        var members = model.LookupSymbols(position, type, includeReducedExtensionMethods: true);
        foreach (var member in members)
        {
            // An instance block method comes reduced (receiver bound): its definition is in the block.
            var definition = member is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : member;
            if (definition is not (IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })) continue;
            if (definition.ContainingType is not { IsExtension: true, ContainingType: { } declaring }) continue;
            if (member.IsStatic != isType && member is not IMethodSymbol { ReducedFrom: not null }) continue;
            result.TryAdd(member.Name, declaring.Name);
        }
        return result;
    }

    /// <summary>The expression before the "." the caret is after (`order.`, `order.Gr`, `order?.Gr`).</summary>
    static ExpressionSyntax? Receiver(SyntaxNode root, int position)
    {
        var token = root.FindToken(Math.Max(0, position - 1));
        if (token.IsKind(SyntaxKind.IdentifierToken) && token.Span.End == position) token = token.GetPreviousToken();
        if (!token.IsKind(SyntaxKind.DotToken) || token.Span.End > position) return null;
        return token.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression,
            MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            QualifiedNameSyntax qualified => qualified.Left,
            _ => null,
        };
    }
}

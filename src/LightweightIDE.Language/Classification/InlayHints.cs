using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LightweightIDE.Language.Classification;

public enum InlayHintKind { Parameter, Type }

/// <summary>A hint shown at <see cref="Position"/> (UTF-16 offset): "count:" before an argument, ": int" after a name.</summary>
public readonly record struct InlayHint(int Position, string Text, InlayHintKind Kind);

/// <summary>
/// Inlay hints like Rider's defaults, kept quiet on purpose:
/// parameter names before literal arguments (numbers, strings, true / false / null, default) when the call has more
/// than one argument or the literal is true / false / null, and not when the name repeats the method name;
/// types after <c>var</c> declarations, <c>foreach (var …)</c>, <c>out var</c> and deconstruction when the
/// initializer does not show the type (no <c>new T()</c>, cast, <c>as</c> or literal); types of implicitly typed
/// lambda parameters.
/// </summary>
public sealed class InlayHintService(WorkspaceService workspace)
{
    public async Task<IReadOnlyList<InlayHint>> GetHintsAsync(string path, string text, int start, int end, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(SourceText.From(text));
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (model is null || root is null) return [];
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        return Collect(model, root, TextSpan.FromBounds(start, end), ct);
    }

    public static IReadOnlyList<InlayHint> Collect(SemanticModel model, SyntaxNode root, TextSpan span, CancellationToken ct)
    {
        var hints = new List<InlayHint>();
        foreach (var node in root.DescendantNodes(span))
        {
            ct.ThrowIfCancellationRequested();
            if (!span.IntersectsWith(node.Span)) continue;
            switch (node)
            {
                case ArgumentListSyntax args:
                    ParameterHints(model, args.Arguments, args.Parent, hints, ct);
                    break;
                case VariableDeclarationSyntax { Type.IsVar: true } declaration:
                    foreach (var variable in declaration.Variables)
                        if (variable.Initializer is { } init && !TypeIsObvious(init.Value))
                            TypeHint(model.GetDeclaredSymbol(variable, ct) as ILocalSymbol, variable.Identifier, model, hints);
                    break;
                case ForEachStatementSyntax { Type.IsVar: true } loop:
                    TypeHint(model.GetDeclaredSymbol(loop, ct), loop.Identifier, model, hints);
                    break;
                case SingleVariableDesignationSyntax designation when designation.Parent is DeclarationExpressionSyntax { Type.IsVar: true }
                    or ParenthesizedVariableDesignationSyntax:
                    TypeHint(model.GetDeclaredSymbol(designation, ct) as ILocalSymbol, designation.Identifier, model, hints);
                    break;
                case SimpleLambdaExpressionSyntax { Parameter.Type: null } lambda:
                    LambdaHint(model, lambda.Parameter, hints, ct);
                    break;
                case ParenthesizedLambdaExpressionSyntax lambda:
                    foreach (var parameter in lambda.ParameterList.Parameters.Where(p => p.Type is null)) LambdaHint(model, parameter, hints, ct);
                    break;
            }
        }
        return hints;
    }

    static void ParameterHints(SemanticModel model, SeparatedSyntaxList<ArgumentSyntax> arguments, SyntaxNode? call, List<InlayHint> hints, CancellationToken ct)
    {
        if (arguments.Count == 0 || call is null) return;
        if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol method) return;
        var methodName = method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon is not null || !IsLiteral(argument.Expression)) continue;
            var parameter = i < method.Parameters.Length ? method.Parameters[i] : null;
            if (parameter is null || parameter.IsParams && i > method.Parameters.Length - 1) break;
            if (parameter.IsParams || parameter.Name.Length < 2) continue;
            var isFlag = argument.Expression.Kind() is SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression or SyntaxKind.NullLiteralExpression;
            if (arguments.Count == 1 && !isFlag) continue;
            if (methodName.EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase)) continue;
            hints.Add(new InlayHint(argument.Span.Start, parameter.Name + ":", InlayHintKind.Parameter));
        }
    }

    static bool IsLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
        DefaultExpressionSyntax => true,
        InterpolatedStringExpressionSyntax => true,
        _ => false,
    };

    /// <summary>The initializer already names the type: no hint.</summary>
    static bool TypeIsObvious(ExpressionSyntax value) => value switch
    {
        ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or CastExpressionSyntax or LiteralExpressionSyntax
            or DefaultExpressionSyntax or TypeOfExpressionSyntax or InterpolatedStringExpressionSyntax => true,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } => true,
        AwaitExpressionSyntax { Expression: ObjectCreationExpressionSyntax } => true,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } => true,
        _ => false,
    };

    static void TypeHint(ILocalSymbol? local, SyntaxToken identifier, SemanticModel model, List<InlayHint> hints)
    {
        if (local?.Type is not { } type || type.IsAnonymousType || type is IErrorTypeSymbol || identifier.IsMissing) return;
        // A var local is always declared nullable (T?); show the type the initializer gives, like Rider.
        if (type.IsReferenceType) type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        hints.Add(new InlayHint(identifier.Span.End, ": " + type.ToMinimalDisplayString(model, identifier.SpanStart), InlayHintKind.Type));
    }

    static void LambdaHint(SemanticModel model, ParameterSyntax parameter, List<InlayHint> hints, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(parameter, ct) is not { Type: { } type } || type is IErrorTypeSymbol || type.IsAnonymousType) return;
        if (parameter.Identifier.ValueText == "_") return;
        hints.Add(new InlayHint(parameter.Identifier.SpanStart, type.ToMinimalDisplayString(model, parameter.SpanStart), InlayHintKind.Type));
    }
}

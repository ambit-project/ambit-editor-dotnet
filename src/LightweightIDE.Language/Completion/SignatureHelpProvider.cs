using System.Xml.Linq;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LightweightIDE.Language.Completion;

/// <summary>One overload: "ReturnType Type.Name(" + parameters + ")", with documentation.</summary>
public sealed record SignatureItem(string Prefix, IReadOnlyList<string> Parameters, string Suffix, string? Documentation,
    IReadOnlyList<string?> ParameterDocumentation);

/// <summary>Overloads of the call at the caret; <see cref="ActiveParameter"/> is the argument being typed.</summary>
public sealed record SignatureHelpResult(IReadOnlyList<SignatureItem> Signatures, int ActiveSignature, int ActiveParameter, int ArgumentListStart);

/// <summary>
/// Parameter info for invocations, object creations (also target-typed <c>new(</c>), base / this constructor calls and
/// indexers: overloads, the active parameter (positional or named) and XML documentation. Built on the semantic model
/// (Roslyn's own signature help service is internal).
/// </summary>
public sealed class SignatureHelpProvider(WorkspaceService workspace)
{
    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    public async Task<SignatureHelpResult?> GetAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(text));
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return null;

        var token = root.FindToken(Math.Max(0, position - 1));
        var argumentList = token.Parent?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>()
            .FirstOrDefault(a => position > Open(a).SpanStart && (Close(a).IsMissing || position <= Close(a).SpanStart));
        if (argumentList is null) return null;

        IReadOnlyList<IMethodSymbol> methods;
        ISymbol? bound;
        switch (argumentList.Parent)
        {
            case InvocationExpressionSyntax invocation:
                methods = model.GetMemberGroup(invocation.Expression, ct).OfType<IMethodSymbol>().ToList();
                bound = model.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case BaseObjectCreationExpressionSyntax creation:
                var type = model.GetTypeInfo(creation, ct).Type as INamedTypeSymbol;
                methods = type?.InstanceConstructors.Where(c => model.IsAccessible(position, c)).ToList() ?? [];
                bound = model.GetSymbolInfo(creation, ct).Symbol;
                break;
            case ConstructorInitializerSyntax initializer:
                var container = model.GetEnclosingSymbol(position, ct)?.ContainingType;
                var target = initializer.IsKind(SyntaxKind.BaseConstructorInitializer) ? container?.BaseType : container;
                methods = target?.InstanceConstructors.Where(c => model.IsAccessible(position, c)).ToList() ?? [];
                bound = model.GetSymbolInfo(initializer, ct).Symbol;
                break;
            case ElementAccessExpressionSyntax access:
                var receiver = model.GetTypeInfo(access.Expression, ct).Type;
                methods = receiver?.GetMembers().OfType<IPropertySymbol>().Where(p => p.IsIndexer).Select(p => p.GetMethod).OfType<IMethodSymbol>().ToList() ?? [];
                bound = null;
                break;
            default:
                return null;
        }
        if (methods.Count == 0) return null;

        // Active argument: commas before the caret; a named argument picks its parameter by name.
        var arguments = argumentList.Arguments;
        var index = arguments.GetSeparators().Count(s => s.SpanStart < position);
        var current = index < arguments.Count ? arguments[index] : null;
        var named = current?.NameColon?.Name.Identifier.ValueText;

        var ordered = methods.OrderBy(m => m.Parameters.Length).ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal).ToList();
        var active = bound is IMethodSymbol b ? ordered.FindIndex(m => SymbolEqualityComparer.Default.Equals(m, b) || SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, b.OriginalDefinition)) : -1;
        if (active < 0 && named is not null) active = ordered.FindIndex(m => m.Parameters.Any(p => p.Name == named));
        if (active < 0)
            active = Math.Max(0, ordered.FindIndex(m => m.Parameters.Length > index || m.Parameters.LastOrDefault()?.IsParams == true));

        var items = ordered.Select(Describe).ToList();
        var parameter = index;
        if (named is not null) parameter = ordered[active].Parameters.ToList().FindIndex(p => p.Name == named);
        else if (ordered[active].Parameters is { Length: > 0 } ps && ps[^1].IsParams && index >= ps.Length) parameter = ps.Length - 1;
        return new SignatureHelpResult(items, active, parameter, Open(argumentList).SpanStart);
    }

    static SyntaxToken Open(BaseArgumentListSyntax list) => list switch
    {
        ArgumentListSyntax l => l.OpenParenToken,
        BracketedArgumentListSyntax b => b.OpenBracketToken,
        _ => list.GetFirstToken(),
    };

    static SyntaxToken Close(BaseArgumentListSyntax list) => list switch
    {
        ArgumentListSyntax l => l.CloseParenToken,
        BracketedArgumentListSyntax b => b.CloseBracketToken,
        _ => list.GetLastToken(),
    };

    static SignatureItem Describe(IMethodSymbol method)
    {
        var name = method.MethodKind == MethodKind.Constructor ? method.ContainingType.ToDisplayString(TypeFormat)
            : method.AssociatedSymbol is IPropertySymbol { IsIndexer: true } ? "this"
            : method.Name + (method.IsGenericMethod ? "<" + string.Join(", ", method.TypeArguments.Select(t => t.ToDisplayString(TypeFormat))) + ">" : "");
        var prefix = method.MethodKind == MethodKind.Constructor ? name + "("
            : $"{method.ReturnType.ToDisplayString(TypeFormat)} {method.ContainingType?.Name}.{name}" + (method.AssociatedSymbol is IPropertySymbol { IsIndexer: true } ? "[" : "(");
        var suffix = method.AssociatedSymbol is IPropertySymbol { IsIndexer: true } ? "]" : ")";
        var docs = Docs(method.GetDocumentationCommentXml(expandIncludes: true));
        var parameters = method.Parameters.Select(p =>
        {
            var modifier = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
            var display = $"{(p.IsParams ? "params " : "")}{modifier}{p.Type.ToDisplayString(TypeFormat)} {p.Name}";
            if (p.HasExplicitDefaultValue) display += " = " + DefaultText(p.ExplicitDefaultValue);
            return display;
        }).ToList();
        var parameterDocs = method.Parameters.Select(p => docs.Parameters.GetValueOrDefault(p.Name)).ToList();
        return new SignatureItem(prefix, parameters, suffix, docs.Summary, parameterDocs);
    }

    static string DefaultText(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    static (string? Summary, Dictionary<string, string> Parameters) Docs(string? xml)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xml)) return (null, parameters);
        try
        {
            var doc = XElement.Parse(xml.Trim().StartsWith("<member", StringComparison.Ordinal) ? xml : $"<member>{xml}</member>");
            foreach (var p in doc.Elements("param"))
                if ((string?)p.Attribute("name") is { } n) parameters[n] = Flatten(p);
            return (doc.Element("summary") is { } s ? Flatten(s) : null, parameters);
        }
        catch (System.Xml.XmlException) { return (null, parameters); }
    }

    /// <summary>Text of a doc element: &lt;see cref="T:X.Y"/&gt; → Y, whitespace collapsed.</summary>
    static string Flatten(XElement element)
    {
        var parts = element.Nodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" or "seealso" } e => ((string?)e.Attribute("cref") ?? (string?)e.Attribute("langword") ?? e.Value).Split(':').Last().Split('.').Last(),
            XElement { Name.LocalName: "paramref" or "typeparamref" } e => (string?)e.Attribute("name") ?? "",
            XElement e => e.Value,
            _ => "",
        });
        return string.Join(" ", string.Concat(parts).Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }
}

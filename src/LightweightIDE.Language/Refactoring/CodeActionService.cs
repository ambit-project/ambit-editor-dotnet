using System.Reflection;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.Language.Refactoring;

/// <summary>A quick fix or refactoring offered at the caret. <see cref="Fixes"/> is the diagnostic id it fixes, if any.</summary>
public sealed record CodeActionItem(int Id, string Title, string? Fixes);

/// <summary>
/// Alt+Enter: Roslyn's own code fixes (add using, implement interface, generate member, …) and refactorings
/// (introduce variable, convert to …) from Microsoft.CodeAnalysis(.CSharp).Features. The providers are created by
/// reflection from their [Export…] types — all of them have parameterless constructors — so no MEF host is needed.
/// </summary>
public sealed class CodeActionService(WorkspaceService workspace, ILogger<CodeActionService> logger)
{
    const int MaxActions = 60;
    static readonly Lazy<(IReadOnlyList<CodeFixProvider> Fixes, IReadOnlyList<CodeRefactoringProvider> Refactorings)> Providers = new(LoadProviders);
    readonly Lock gate = new();
    readonly Dictionary<int, (CodeAction Action, Solution Solution)> offered = [];
    int nextId;

    static (IReadOnlyList<CodeFixProvider>, IReadOnlyList<CodeRefactoringProvider>) LoadProviders()
    {
        var fixes = new List<CodeFixProvider>();
        var refactorings = new List<CodeRefactoringProvider>();
        foreach (var name in new[] { "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.Features" })
        {
            Type[] types;
            try { types = Assembly.Load(name).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            catch (Exception) { continue; }
            foreach (var type in types.Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                try
                {
                    if (typeof(CodeFixProvider).IsAssignableFrom(type) && IsCSharpExport(type, "ExportCodeFixProviderAttribute"))
                        fixes.Add((CodeFixProvider)Activator.CreateInstance(type)!);
                    else if (typeof(CodeRefactoringProvider).IsAssignableFrom(type) && IsCSharpExport(type, "ExportCodeRefactoringProviderAttribute"))
                        refactorings.Add((CodeRefactoringProvider)Activator.CreateInstance(type)!);
                }
                catch (Exception) { } // a provider that cannot be created is simply not offered
            }
        }
        return (fixes, refactorings);
    }

    static bool IsCSharpExport(Type type, string attributeName) =>
        type.GetCustomAttributes().Any(a => a.GetType().Name == attributeName
            && (a.GetType().GetProperty("Languages")?.GetValue(a) as string[] ?? []).Contains(LanguageNames.CSharp));

    public static int ProviderCount => Providers.Value.Fixes.Count + Providers.Value.Refactorings.Count;

    /// <summary>Fixes for the diagnostics on the caret's line, then refactorings at the caret (or selection).</summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetActionsAsync(string path, string text, int start, int end, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        var source = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (source.ToString() != text) document = document.WithText(SourceText.From(text));
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) return [];
        var line = source.Lines.GetLineFromPosition(Math.Min(start, source.Length)).Span;
        var (fixProviders, refactoringProviders) = Providers.Value;

        var found = new List<(CodeAction Action, string? Fixes, int Rank)>();
        void Add(CodeAction action, string? fixes, int rank)
        {
            // Groups (Add using → several namespaces) are shown as their choices.
            if (!action.NestedActions.IsDefaultOrEmpty)
                foreach (var nested in action.NestedActions) Add(nested, fixes, rank);
            // Adding a using is what one wants for a missing type almost always: first, like in Rider.
            else lock (found) found.Add((action, fixes, action.Title.StartsWith("using ", StringComparison.Ordinal) ? rank - 1 : rank));
        }

        // In a type's header (name, primary constructor, base list — often over several lines) the fixes of the whole
        // header are offered: "Implement interface" on the class name, as in Rider.
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is not null && TypeHeader(root, Math.Min(start, source.Length)) is { } header) line = TextSpan.FromBounds(Math.Min(line.Start, header.Start), Math.Max(line.End, header.End));
        var diagnostics = model.GetDiagnostics(line, ct)
            .Where(d => d.Location.SourceSpan.IntersectsWith(line) || d.Location.SourceSpan.IsEmpty)
            .OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1)
            .ToList();
        foreach (var diagnostic in diagnostics)
        {
            var rank = diagnostic.Severity == DiagnosticSeverity.Error ? 0 : 1;
            foreach (var provider in fixProviders.Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)))
            {
                try
                {
                    await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => Add(a, diagnostic.Id, rank), ct))
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException) { logger.LogDebug(e, "Code fix {Provider} failed", provider.GetType().Name); }
            }
        }

        var span = TextSpan.FromBounds(Math.Min(start, source.Length), Math.Min(Math.Max(start, end), source.Length));
        foreach (var provider in refactoringProviders)
        {
            try
            {
                await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, a => Add(a, null, 2), ct)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogDebug(e, "Refactoring {Provider} failed", provider.GetType().Name); }
        }

        var items = new List<CodeActionItem>();
        lock (gate)
        {
            offered.Clear();
            foreach (var (action, fixes, _) in found.OrderBy(f => f.Rank).DistinctBy(f => f.Action.Title).Take(MaxActions))
            {
                var id = ++nextId;
                offered[id] = (action, document.Project.Solution);
                items.Add(new CodeActionItem(id, action.Title, fixes));
            }
        }
        return items;
    }

    /// <summary>The header of the type declaration the position is in (from its start to the '{'), or null outside one.</summary>
    static TextSpan? TypeHeader(SyntaxNode root, int position, bool anywhereInType = false)
    {
        var type = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null) return null;
        var headerEnd = type.OpenBraceToken.IsMissing ? type.Span.End : type.OpenBraceToken.SpanStart;
        if (!anywhereInType && position > headerEnd) return null;
        return TextSpan.FromBounds(type.SpanStart, headerEnd);
    }

    /// <summary>Diagnostics "member not implemented": interface members, abstract members.</summary>
    static readonly string[] MissingMemberIds = ["CS0535", "CS0534", "CS0737", "CS0738"];

    /// <summary>
    /// Implement Missing Members (Ctrl+I) from anywhere in a class: the implement-interface / abstract-class fixes of
    /// the enclosing type, the plain "Implement interface" / "Implement abstract class" first.
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetImplementMembersAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text) document = document.WithText(SourceText.From(text));
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null || TypeHeader(root, Math.Min(position, text.Length), anywhereInType: true) is not { } header) return [];
        var all = await GetActionsAsync(path, text, header.Start, header.Start, ct).ConfigureAwait(false);
        return all.Where(i => i.Fixes is { } id && MissingMemberIds.Contains(id))
            .OrderBy(i => i.Title.StartsWith("Implement interface", StringComparison.Ordinal) && !i.Title.Contains("through", StringComparison.Ordinal)
                          || i.Title.StartsWith("Implement abstract class", StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    /// <summary>The file changes an offered action makes (new files have IsNew = true).</summary>
    public async Task<IReadOnlyList<FileEdit>> ApplyAsync(int id, CancellationToken ct)
    {
        (CodeAction Action, Solution Solution) entry;
        lock (gate)
            if (!offered.TryGetValue(id, out entry)) throw new InvalidOperationException("The action is no longer available. Press Alt+Enter again.");
        var operations = await entry.Action.GetOperationsAsync(ct).ConfigureAwait(false);
        var changed = operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution;
        if (changed is null) return [];

        var edits = new Dictionary<string, FileEdit>(StringComparer.Ordinal);
        foreach (var project in changed.GetChanges(entry.Solution).GetProjectChanges())
        {
            foreach (var docId in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var before = entry.Solution.GetDocument(docId);
                var after = changed.GetDocument(docId);
                if (before?.FilePath is not { } file || after is null || edits.ContainsKey(file)) continue;
                var oldText = (await before.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                var newText = (await after.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                edits[file] = new FileEdit(file, oldText, newText, Formatting.TextDiff.Compute(oldText, newText).Count, null);
            }
            foreach (var docId in project.GetAddedDocuments())
            {
                var added = changed.GetDocument(docId);
                if (added is null) continue;
                var dir = added.FilePath is { } p ? System.IO.Path.GetDirectoryName(p)!
                    : System.IO.Path.GetDirectoryName(project.NewProject.FilePath ?? "/")!;
                var file = added.FilePath ?? System.IO.Path.Combine(new[] { dir }.Concat(added.Folders).Append(added.Name).ToArray());
                var text = (await added.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                edits[file] = new FileEdit(file, "", text, 1, null, IsNew: true);
            }
        }
        return edits.Values.ToList();
    }
}

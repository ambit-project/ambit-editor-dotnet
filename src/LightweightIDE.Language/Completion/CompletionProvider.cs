using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Language.Completion;

/// <param name="Priority">Roslyn's MatchPriority (Preselect is huge, keywords negative).</param>
/// <param name="TargetMatch">Fits the expected type (Roslyn's TargetTypeMatch tag).</param>
/// <param name="InScope">False for types and extension members from namespaces that are not imported (committing adds the using).</param>
/// <param name="Smart">Why the type fits a generic constraint or the expected interface ("implements IShop").</param>
public sealed record CompletionEntry(string DisplayText, string FilterText, string Kind, string? Suffix, int Index,
    int Priority = 0, bool TargetMatch = false, bool InScope = true, string? SortText = null, string? Smart = null);

/// <summary>Completion items for one position plus what is needed to commit one of them.</summary>
public sealed class CompletionSession
{
    internal Document Document { get; init; } = null!;
    internal CompletionList List { get; init; } = null!;
    internal CompletionService Service { get; init; } = null!;
    public required IReadOnlyList<CompletionEntry> Items { get; init; }
    /// <summary>Span (0-based characters) the user is typing; filter items with the text in it.</summary>
    public required int SpanStart { get; init; }
    public required int SpanEnd { get; init; }
    /// <summary>Not from Roslyn (e.g. NuGet package names): committing replaces the span with the item text.</summary>
    public bool IsPlainText => Service is null;
}

public sealed record TextEdit(int Start, int End, string NewText, int? CaretPosition);

/// <summary>Basic completion via Roslyn's CompletionService (same engine as Visual Studio and C# Dev Kit).</summary>
public sealed class CompletionProvider
{
    readonly WorkspaceService workspace;
    readonly LightweightIDE.Language.Analysis.CodeModel? codeModel;
    readonly ILogger logger;
    CancellationTokenSource? warmupCts;

    public CompletionProvider(WorkspaceService workspace, LightweightIDE.Language.Analysis.CodeModel? codeModel = null, ILogger<CompletionProvider>? logger = null)
    {
        this.workspace = workspace;
        this.codeModel = codeModel;
        this.logger = logger ?? NullLogger<CompletionProvider>.Instance;
        workspace.StateChanged += OnWorkspaceState;
    }

    void OnWorkspaceState()
    {
        warmupCts?.Cancel();
        if (workspace.State != WorkspaceState.Ready) return;
        var cts = warmupCts = new CancellationTokenSource();
        _ = Task.Run(() => WarmUpAsync(cts.Token), cts.Token);
    }

    /// <summary>
    /// Builds Roslyn's index of types and extension methods from every project reference (NuGet packages, the
    /// framework, other projects) right after the solution loads. Roslyn builds that index during the first list that needs it, so without the warm-up the first "." in a large
    /// solution waits for it (extension methods from packages such as builder.Services.AddSerilog, unimported types).
    /// </summary>
    /// <returns>Projects warmed up.</returns>
    public async Task<int> WarmUpAsync(CancellationToken ct)
    {
        if (workspace.CurrentSolution is not { } solution) return 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        const string text = "static class __CompletionWarmup { static void M(object o) { o.; } static void N() { __X } }";
        var memberPosition = text.IndexOf("o.", StringComparison.Ordinal) + 2;
        var typePosition = text.IndexOf("__X", StringComparison.Ordinal) + 3;
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A document added to an immutable copy of the solution: nothing is written to the workspace.
                var document = project.AddDocument("__CompletionWarmup.cs", text, filePath: Path.Combine(Path.GetDirectoryName(project.FilePath) ?? "", "__CompletionWarmup.cs"));
                if (CompletionService.GetService(document) is not { } service) continue;
                // Roslyn builds the index of a reference on the first list that needs it (synchronously).
                await service.GetCompletionsAsync(document, memberPosition, CompletionTrigger.Invoke, cancellationToken: ct).ConfigureAwait(false);
                await service.GetCompletionsAsync(document, typePosition, CompletionTrigger.Invoke, cancellationToken: ct).ConfigureAwait(false);
                count++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Completion warm-up failed for {Project}", project.Name);
            }
        }
        logger.LogInformation("Completion index warmed up: {Count} project(s) in {Ms} ms", count, watch.ElapsedMilliseconds);
        return count;
    }

    public async Task<CompletionSession?> GetCompletionsAsync(string path, string text, int position, bool explicitInvoke, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        var service = CompletionService.GetService(document);
        if (service is null) return null;
        if (!explicitInvoke)
        {
            // In a generic argument list (AddScoped<IShop, |) Roslyn does not trigger on ',' / ' ': the smart types are wanted there.
            var typedChar = text[Math.Max(0, position - 1)];
            if (typedChar is ',' or ' ' or '<' && SmartTypes.FindGenericArgument(text, position) is not null) explicitInvoke = true;
            else if (!service.ShouldTriggerCompletion(SourceText.From(text), position, CompletionTrigger.CreateInsertionTrigger(typedChar)))
                return null;
        }
        var trigger = explicitInvoke ? CompletionTrigger.Invoke : CompletionTrigger.CreateInsertionTrigger(text[Math.Max(0, position - 1)]);
        var list = await service.GetCompletionsAsync(document, position, trigger, cancellationToken: ct).ConfigureAwait(false);
        if (list.ItemsList.Count == 0) return null;
        IReadOnlyDictionary<string, string> smart;
        try { smart = await SmartTypes.FindAsync(document, text, position, ct, codeModel).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { smart = new Dictionary<string, string>(); }
        IReadOnlyDictionary<string, string> extensionMembers;
        try { extensionMembers = await ExtensionMembers.FindAsync(document, position, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { extensionMembers = new Dictionary<string, string>(); }
        var items = list.ItemsList
            .Select((item, i) =>
            {
                // Items from namespaces that are not imported carry the namespace as inline description; committing one adds
                // the using. Extension members (classic and C# 14 extension blocks) are recognized by their receiver.
                var inScope = string.IsNullOrEmpty(item.InlineDescription)
                    || !(item.Properties.ContainsKey("ReceiverKey") || item.Tags.Any(t => t is "Class" or "Structure" or "Interface" or "Enum" or "Delegate" or "Record" or "RecordStruct" or "ExtensionMethod"));
                var name = item.DisplayText;
                var reason = smart.TryGetValue(name, out var why) ? why : null;
                var suffix = item.InlineDescription is { Length: > 0 } d ? d : null;
                // Members of C# 14 extension blocks: "extension · OrderExtensions" (in scope) or "extension · Lib.Other"
                // (the namespace the commit imports). Classic `this` extension methods stay as they were.
                if (item.Tags.Any(t => t is "Property" or "Method" or "ExtensionMethod"))
                {
                    if (inScope && extensionMembers.TryGetValue(name, out var declaring)) suffix = "extension · " + declaring;
                    else if (!inScope && ExtensionMembers.IsBlockMember(item)) suffix = "extension · " + suffix;
                }
                return new CompletionEntry(item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, item.FilterText,
                    item.Tags.FirstOrDefault() ?? "", reason ?? suffix, i,
                    item.Rules.MatchPriority, item.Tags.Contains("TargetTypeMatch"), inScope, item.SortText, reason);
            })
            .ToList();
        return new CompletionSession
        {
            Document = document,
            List = list,
            Service = service,
            Items = items,
            SpanStart = list.Span.Start,
            SpanEnd = list.Span.End,
        };
    }

    /// <summary>The text change for committing an item (handles generics, overrides, imports-free items, etc.).</summary>
    /// <summary>What the user picked rises in later lists.</summary>
    public CompletionUsage Usage { get; } = new();

    /// <summary>Signature and documentation of an item (shown next to the list).</summary>
    public async Task<string?> GetDescriptionAsync(CompletionSession session, CompletionEntry entry, CancellationToken ct)
    {
        if (session.IsPlainText || entry.Index >= session.List.ItemsList.Count) return null;
        var description = await session.Service.GetDescriptionAsync(session.Document, session.List.ItemsList[entry.Index], ct).ConfigureAwait(false);
        return description?.Text is { Length: > 0 } text ? text : null;
    }

    public async Task<TextEdit> GetCommitEditAsync(CompletionSession session, CompletionEntry entry, CancellationToken ct)
    {
        Usage.Record(entry.DisplayText);
        if (session.IsPlainText) return new TextEdit(session.SpanStart, session.SpanEnd, entry.DisplayText, null);
        var item = session.List.ItemsList[entry.Index];
        var change = await session.Service.GetChangeAsync(session.Document, item, commitCharacter: null, ct).ConfigureAwait(false);
        var tc = change.TextChange;
        return new TextEdit(tc.Span.Start, tc.Span.End, tc.NewText ?? "", change.NewPosition);
    }

    /// <summary>Filters items by what the user typed (prefix first, then camel-case/substring).</summary>
    /// <param name="keepOrder">Keep the source order among equal matches (NuGet: most downloaded / newest first).</param>
    public static IReadOnlyList<CompletionEntry> Filter(IReadOnlyList<CompletionEntry> items, string typed, bool keepOrder = false, CompletionUsage? usage = null)
    {
        if (!keepOrder) return CompletionRanker.Rank(items, typed, usage);
        if (typed.Length == 0)
            return keepOrder ? items.OrderBy(i => i.Index).ToList() : items.OrderBy(i => i.FilterText, StringComparer.OrdinalIgnoreCase).ToList();
        var matches = items.Select(i => (Item: i, Score: Score(i.FilterText, typed))).Where(x => x.Score > 0).OrderByDescending(x => x.Score);
        return (keepOrder
                ? matches.ThenBy(x => x.Item.Index)
                : matches.ThenBy(x => x.Item.FilterText.Length).ThenBy(x => x.Item.FilterText, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Item)
            .ToList();
    }

    static int Score(string candidate, string typed)
    {
        if (candidate.StartsWith(typed, StringComparison.Ordinal)) return 400;
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 300;
        // camel humps: "SB" -> StringBuilder
        var humps = new string(candidate.Where(char.IsUpper).ToArray());
        if (humps.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 200;
        if (candidate.Contains(typed, StringComparison.OrdinalIgnoreCase)) return 100;
        return 0;
    }
}

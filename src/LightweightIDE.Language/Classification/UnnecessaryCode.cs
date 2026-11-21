using System.Collections.Immutable;
using System.Reflection;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LightweightIDE.Language.Classification;

/// <summary>A range of code that does nothing (UTF-16 offsets), with the Roslyn diagnostic id that found it.</summary>
public readonly record struct UnnecessarySpan(int Start, int End, string Id, string Message);

/// <summary>
/// Code the editor shows faded, as Visual Studio does: unnecessary usings (IDE0005), unused private members (IDE0051,
/// IDE0052), unused parameters (IDE0060) and values assigned but never read (IDE0059). The analyzers are the ones
/// Visual Studio runs, from Microsoft.CodeAnalysis(.CSharp).Features; .editorconfig severities apply (none = off).
/// Only documents of a loaded project are analyzed: without references a misc file would fade code by mistake.
/// </summary>
public sealed class UnnecessaryCode(WorkspaceService workspace)
{
    public static readonly ImmutableHashSet<string> Ids = ["IDE0005", "IDE0051", "IDE0052", "IDE0059", "IDE0060"];

    static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> Analyzers = new(LoadAnalyzers);

    static ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers()
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var name in new[] { "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.Features" })
        {
            Type[] types;
            try { types = Assembly.Load(name).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            catch (Exception) { continue; }
            foreach (var type in types.Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                try
                {
                    var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
                    if (analyzer.SupportedDiagnostics.Any(d => Ids.Contains(d.Id))) analyzers.Add(analyzer);
                }
                catch (Exception) { } // an analyzer that cannot be created is simply not run
            }
        }
        return analyzers.ToImmutable();
    }

    public async Task<IReadOnlyList<UnnecessarySpan>> GetAsync(string path, string text, CancellationToken ct, bool includeMiscellaneous = false)
    {
        if (Analyzers.Value.IsEmpty) return [];
        var document = workspace.FindDocument(path);
        if (document is null)
        {
            if (!includeMiscellaneous) return [];
            document = workspace.CreateMiscDocument(path, text);
        }
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(SourceText.From(text));
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) return [];
        var withAnalyzers = model.Compilation.WithAnalyzers(Analyzers.Value,
            new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions, onAnalyzerException: null,
                concurrentAnalysis: false, logAnalyzerExecutionTime: false));
        var diagnostics = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, ct).ConfigureAwait(false);

        var result = new List<UnnecessarySpan>();
        foreach (var d in diagnostics)
        {
            if (!Ids.Contains(d.Id) || d.Severity == DiagnosticSeverity.Error || !d.Location.IsInSource
                || d.Location.SourceTree != model.SyntaxTree || !d.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
                continue;
            var message = d.GetMessage();
            foreach (var span in FadedSpans(d))
                result.Add(new UnnecessarySpan(span.Start, span.End, d.Id, message));
        }
        return result.OrderBy(s => s.Start).ToList();
    }

    /// <summary>
    /// What to fade: the additional locations listed in the "Unnecessary" property (e.g. the whole unused method),
    /// otherwise the diagnostic's own location.
    /// </summary>
    static IEnumerable<TextSpan> FadedSpans(Diagnostic d)
    {
        if (d.Properties.TryGetValue(WellKnownDiagnosticTags.Unnecessary, out var indexes) && indexes is not null)
        {
            var spans = indexes.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(i => int.TryParse(i, out var n) && n >= 0 && n < d.AdditionalLocations.Count ? d.AdditionalLocations[n] : null)
                .Where(l => l is { IsInSource: true } && l.SourceTree == d.Location.SourceTree)
                .Select(l => l!.SourceSpan)
                .ToList();
            if (spans.Count > 0) return spans;
        }
        return [d.Location.SourceSpan];
    }
}

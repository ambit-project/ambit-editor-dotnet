using LightweightIDE.DotNet.Build;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using Severity = LightweightIDE.DotNet.Build.DiagnosticSeverity;

namespace LightweightIDE.Language.Diagnostics;

/// <summary>A live diagnostic with its 0-based character span in the document, for editor underlines.</summary>
public sealed record LiveDiagnostic(BuildDiagnostic Diagnostic, int Start, int End);

/// <summary>Compiler diagnostics for open documents and whole projects, computed off the UI thread.</summary>
public sealed class DiagnosticsService(WorkspaceService workspace)
{
    /// <summary>Syntax + semantic diagnostics of one document (semantic only when it belongs to a loaded project).</summary>
    public async Task<IReadOnlyList<LiveDiagnostic>> GetDocumentDiagnosticsAsync(string path, string text, CancellationToken ct)
    {
        var document = workspace.FindDocument(path);
        IEnumerable<Diagnostic> diagnostics;
        if (document is null)
        {
            // Not in any project: syntax errors only (semantic errors would be noise without references).
            var misc = workspace.CreateMiscDocument(path, text);
            var tree = await misc.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
            diagnostics = tree!.GetDiagnostics(ct);
        }
        else
        {
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            diagnostics = model!.GetDiagnostics(cancellationToken: ct);
        }
        return diagnostics
            .Where(d => d.Severity != RoslynSeverity.Hidden && d.Location.IsInSource)
            .Select(d => new LiveDiagnostic(Convert(d, document?.Project.Name), d.Location.SourceSpan.Start, d.Location.SourceSpan.End))
            .OrderBy(d => d.Start)
            .ToList();
    }

    /// <summary>All compiler diagnostics of one project (compiles it; can take seconds on big projects).</summary>
    public async Task<IReadOnlyList<BuildDiagnostic>> GetProjectDiagnosticsAsync(string projectPath, CancellationToken ct)
    {
        var project = workspace.CurrentSolution?.Projects.FirstOrDefault(p => string.Equals(p.FilePath, projectPath, StringComparison.Ordinal));
        if (project is null) return [];
        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        return compilation!.GetDiagnostics(ct)
            .Where(d => d.Severity is RoslynSeverity.Error or RoslynSeverity.Warning)
            .Select(d => Convert(d, project.Name))
            .ToList();
    }

    public static BuildDiagnostic Convert(Diagnostic d, string? project)
    {
        var span = d.Location.GetLineSpan();
        var severity = d.Severity switch
        {
            RoslynSeverity.Error => Severity.Error,
            RoslynSeverity.Warning => Severity.Warning,
            _ => Severity.Info,
        };
        return new BuildDiagnostic(severity, d.Id, d.GetMessage(), span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            project, DiagnosticSource.Live);
    }
}

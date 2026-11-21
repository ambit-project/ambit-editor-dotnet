using LightweightIDE.DotNet.Formatting;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis.Formatting;

namespace LightweightIDE.Language.Formatting;

public enum FormatterEngine { Auto, CSharpier, Roslyn }

/// <summary>"Formatting" configuration section.</summary>
public sealed class FormattingOptions
{
    /// <summary>auto: CSharpier when the repository (or the user) has it as a dotnet tool, otherwise Roslyn.</summary>
    public FormatterEngine Engine { get; set; } = FormatterEngine.Auto;
    public bool FormatOnSave { get; set; }

    /// <summary>Format the C# file when a typed '}' closes a block, if the file has no syntax errors.</summary>
    public bool FormatOnType { get; set; } = true;
}

/// <summary>Formatted text, or an error; <see cref="Engine"/> says which formatter ran.</summary>
public sealed record FormatResult(string? Text, string Engine, string? Error)
{
    public bool Changed(string original) => Text is not null && !string.Equals(Text, original, StringComparison.Ordinal);
}

/// <summary>Formats a document with CSharpier (dotnet tool) or Roslyn's formatter (which honours .editorconfig).</summary>
public sealed class FormattingService(WorkspaceService workspace, CSharpierClient csharpier, FormattingOptions options)
{
    static readonly string[] CSharpierExtensions = [".cs", ".csx", ".csproj", ".props", ".targets", ".xml", ".config"];

    /// <summary>True when the C# text parses without syntax errors (no semantic check, no compilation).</summary>
    public static bool IsSyntaxValid(string text) =>
        !Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetDiagnostics()
            .Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    public static bool CanFormat(string path) => CSharpierExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<FormatResult> FormatAsync(string path, string text, CancellationToken ct)
    {
        var isCSharp = Path.GetExtension(path) is ".cs" or ".csx";
        var tool = options.Engine == FormatterEngine.Roslyn ? null : CSharpierClient.Find(path);
        if (tool is not null)
        {
            try
            {
                var result = await csharpier.FormatAsync(tool, path, text, ct).ConfigureAwait(false);
                return result.Status switch
                {
                    CSharpierStatus.Formatted => new FormatResult(result.Text, tool.Description, null),
                    CSharpierStatus.Ignored => new FormatResult(text, tool.Description, null),
                    CSharpierStatus.UnsupportedFile when isCSharp => await RoslynAsync(path, text, ct).ConfigureAwait(false),
                    CSharpierStatus.UnsupportedFile => new FormatResult(null, tool.Description, "CSharpier does not support this file type."),
                    _ => new FormatResult(null, tool.Description, result.Error ?? "CSharpier could not format the file."),
                };
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new FormatResult(null, tool.Description, e.Message);
            }
        }
        if (options.Engine == FormatterEngine.CSharpier)
            return new FormatResult(null, "CSharpier", "CSharpier is not installed. Add it with `dotnet tool install csharpier` (local) or `dotnet tool install -g csharpier`.");
        if (!isCSharp) return new FormatResult(null, "Roslyn", "Only C# files can be formatted without CSharpier.");
        return await RoslynAsync(path, text, ct).ConfigureAwait(false);
    }

    async Task<FormatResult> RoslynAsync(string path, string text, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(text));
        var formatted = await Formatter.FormatAsync(document, cancellationToken: ct).ConfigureAwait(false);
        return new FormatResult((await formatted.GetTextAsync(ct).ConfigureAwait(false)).ToString(), "Roslyn formatter (.editorconfig)", null);
    }
}

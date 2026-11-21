using LightweightIDE.App.Views;
using LightweightIDE.Editor;
using LightweightIDE.Editor.FileOperations;
using LightweightIDE.Language.Formatting;
using LightweightIDE.Language.Refactoring;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>Navigation history, rename, formatting and NuGet packages.</summary>
public sealed partial class MainWindow
{
    readonly NavigationHistory history = new();
    PackagesWindow? packagesWindow;
    bool renaming;

    // ---------- navigation history ----------

    void MoveInHistory(bool back)
    {
        var current = editors.Active?.Position();
        while (true)
        {
            var point = back ? history.GoBack(current) : history.GoForward(current);
            if (point is null) break;
            // A file deleted outside the IDE: skip its entries.
            if (!File.Exists(point.Path))
            {
                current = null;
                continue;
            }
            editors.Open(point.Path, point.Line, point.Column);
            break;
        }
        UpdateHistoryState();
    }

    void UpdateHistoryState()
    {
        Enable("navigate-back", history.CanGoBack);
        Enable("navigate-forward", history.CanGoForward);
    }

    // ---------- save / format ----------

    async Task SaveActiveAsync()
    {
        if (editors.Active is not { } tab) return;
        if (formattingOptions.FormatOnSave && FormattingService.CanFormat(tab.Path)) await FormatDocumentAsync(tab, quiet: true);
        editors.TrySave(tab);
    }

    /// <summary>Formats the tab with CSharpier (dotnet tool) or Roslyn and applies only the changed parts.</summary>
    /// <summary>A typed '}' closed a block: format the whole file, but only when it parses without syntax errors.</summary>
    async Task FormatOnTypeAsync(EditorTab tab)
    {
        if (!formattingOptions.FormatOnType || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        var text = tab.GetText();
        if (!await Task.Run(() => FormattingService.IsSyntaxValid(text))) return;
        if (tab.GetText() != text) return; // typed on meanwhile; the next '}' tries again
        await FormatDocumentAsync(tab, quiet: true);
    }

    /// <summary>Explorer → Format File: opens the file, formats it and saves it when it had no unsaved changes.</summary>
    async Task FormatFileAsync(string path)
    {
        if (!FormattingService.CanFormat(path))
        {
            Status("Formatting supports C# and MSBuild / XML files.");
            return;
        }
        if (editors.Open(path) is not { } tab) return;
        var wasDirty = tab.IsDirty;
        if (await FormatDocumentAsync(tab) && !wasDirty && tab.IsDirty) editors.TrySave(tab);
    }

    async Task<bool> FormatDocumentAsync(EditorTab tab, bool quiet = false)
    {
        if (!FormattingService.CanFormat(tab.Path))
        {
            if (!quiet) Status("Formatting supports C# and MSBuild / XML files.");
            return false;
        }
        var text = tab.GetText();
        Status("Formatting…", 30);
        FormatResult result;
        try
        {
            result = await Task.Run(() => formatting.FormatAsync(tab.Path, text, CancellationToken.None));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Formatting failed");
            Status("Formatting failed: " + e.Message, 10);
            return false;
        }
        if (result.Error is not null)
        {
            Status($"{result.Engine}: {result.Error}", 12);
            return false;
        }
        if (!result.Changed(text))
        {
            if (!quiet) Status($"Already formatted ({result.Engine}).");
            return true;
        }
        if (!tab.ApplyEdits(text, TextDiff.Compute(text, result.Text!)))
        {
            if (!quiet) Status("The file changed while it was being formatted. Format again.");
            return false;
        }
        Status($"Formatted with {result.Engine}. Ctrl+Z undoes it.", quiet ? 3 : 5);
        return true;
    }

    // ---------- rename ----------

    async Task RenameSymbolAsync()
    {
        if (renaming || editors.Active is not { } tab || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        var workspace = session.Workspace;
        if (session.IsOpen && workspace.State is WorkspaceState.Loading or WorkspaceState.NotLoaded)
        {
            session.EnsureWorkspaceLoaded();
            Status("Projects are still loading. Rename needs the whole solution, try again in a moment.");
            return;
        }
        renaming = true;
        try
        {
            // Unsaved changes of every open file must be part of the solution Roslyn renames in.
            foreach (var open in editors.Tabs.Where(t => t.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                workspace.UpdateText(open.Path, open.GetText());

            var text = tab.GetText();
            var position = TextOffsets.CharToUtf16(text, tab.CursorOffset);
            var target = await Task.Run(() => rename.GetTargetAsync(tab.Path, text, position, CancellationToken.None));
            if (target is null)
            {
                Status("No symbol under the caret.");
                return;
            }
            if (target.Error is not null)
            {
                await Ui.ShowErrorAsync(window, "Cannot rename", target.Error);
                return;
            }

            var newName = await Ui.PromptAsync(window, "Rename Symbol", $"New name for {target.Kind} {target.Display}", target.Name,
                n => n == target.Name ? "Enter a different name." : RenameService.ValidateName(n));
            if (newName is null) return;

            Status($"Finding everything named {target.Name}…", 60);
            var plan = await Task.Run(() => rename.PlanAsync(tab.Path, text, position, newName, CancellationToken.None));
            if (plan.Files.Count == 0)
            {
                Status("Nothing to rename.");
                return;
            }

            var fileRename = plan.Files.FirstOrDefault(f => f.NewPath is not null);
            var choice = await Ui.ChooseAsync(window, $"Rename {target.Name} to {newName}?", Describe(plan),
                fileRename is null ? ["Cancel", "Rename"] : ["Cancel", "Rename", $"Rename and rename file to {Path.GetFileName(fileRename.NewPath)}"], 0, 1);
            if (choice <= 0) return;
            await ApplyRenameAsync(plan, renameFiles: choice == 2);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Rename failed");
            await Ui.ShowErrorAsync(window, "Rename failed", e.Message);
        }
        finally { renaming = false; }
    }

    string Describe(RenamePlan plan)
    {
        string Relative(string p) => session.Folder is { } f && p.StartsWith(f, StringComparison.Ordinal) ? Path.GetRelativePath(f, p) : p;
        var lines = plan.Files.Take(15).Select(f => $"  {Relative(f.Path)}  ({f.Changes})").ToList();
        if (plan.Files.Count > 15) lines.Add($"  … and {plan.Files.Count - 15} more");
        var open = plan.Files.Count(f => editors.Find(f.Path) is not null);
        var closed = plan.Files.Count - open;
        var how = (open, closed) switch
        {
            (> 0, > 0) => $"{open} open file(s) are changed in the editor (unsaved, Ctrl+Z undoes); {closed} other file(s) are written to disk.",
            (> 0, 0) => "The files are open: they are changed in the editor (unsaved, Ctrl+Z undoes).",
            _ => "The files are written to disk.",
        };
        return $"{plan.Changes} changed line(s) in {plan.Files.Count} file(s):\n{string.Join("\n", lines)}\n\n{how}";
    }

    async Task ApplyRenameAsync(RenamePlan plan, bool renameFiles)
    {
        var result = ApplyFileEdits(plan.Files);
        var moved = 0;
        if (renameFiles)
        {
            foreach (var file in plan.Files.Where(f => f.NewPath is not null && !result.Skipped.Contains(f.Path)))
            {
                if (File.Exists(file.NewPath) || !File.Exists(file.Path)) continue;
                File.Move(file.Path, file.NewPath!);
                editors.PathRenamed(file.Path, file.NewPath!);
                history.Rename(file.Path, file.NewPath!);
                breakpoints.Rename(file.Path, file.NewPath!);
                explorer.RefreshPaths([file.Path, file.NewPath!]);
                moved++;
            }
            // Document paths changed: the workspace has to read the projects again.
            if (moved > 0) await session.ReloadProjectsAsync();
        }

        editors.RefreshAnalysis();
        UpdateTitle();
        var summary = $"Renamed {plan.OldName} → {plan.NewName}: {result.InEditor} file(s) in the editor (unsaved), {result.OnDisk} on disk"
            + (moved > 0 ? $", {moved} file(s) renamed" : "") + ".";
        await ReportSkippedAsync(summary, result.Skipped);
    }

    sealed record EditResult(int InEditor, int OnDisk, int Created, List<string> Skipped);

    /// <summary>
    /// Applies file changes computed by Roslyn: open files change in the editor (one undo step, left unsaved), closed
    /// files are written to disk keeping their encoding and line endings, new files are created. A file whose content is
    /// no longer what Roslyn saw is skipped, never overwritten.
    /// </summary>
    EditResult ApplyFileEdits(IReadOnlyList<FileEdit> files)
    {
        var skipped = new List<string>();
        int inEditor = 0, onDisk = 0, created = 0;
        foreach (var file in files)
        {
            if (file.IsNew)
            {
                if (File.Exists(file.Path))
                {
                    skipped.Add(file.Path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                File.WriteAllText(file.Path, file.NewText);
                explorer.RefreshPaths([file.Path]);
                created++;
                continue;
            }
            var edits = TextDiff.Compute(file.OldText, file.NewText);
            if (editors.Find(file.Path) is { } open)
            {
                if (open.ApplyEdits(file.OldText, edits)) inEditor++;
                else skipped.Add(file.Path);
                continue;
            }
            try
            {
                var content = TextFile.Read(file.Path);
                if (Normalize(content.Text) != Normalize(file.OldText))
                {
                    skipped.Add(file.Path);
                    continue;
                }
                TextFile.Write(file.Path, file.NewText, content.Encoding, content.HasBom, content.LineEnding);
                session.Workspace.RefreshFromDisk(file.Path);
                onDisk++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Cannot write {Path}", file.Path);
                skipped.Add(file.Path);
            }
        }
        return new EditResult(inEditor, onDisk, created, skipped);
    }

    async Task ReportSkippedAsync(string summary, List<string> skipped)
    {
        if (skipped.Count > 0)
            await Ui.ShowErrorAsync(window, "Some files were not changed",
                $"{summary}\n\nThese files changed after the change was computed (or already exist) and were left as they are:\n{string.Join("\n", skipped)}");
        else Status(summary, 12);
    }

    // ---------- Alt+Enter ----------

    ActionMenu? actionMenu;

    async Task ShowCodeActionsAsync()
    {
        if (editors.Active is not { } tab || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var open in editors.Tabs.Where(t => t.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            session.Workspace.UpdateText(open.Path, open.GetText());
        var text = tab.GetText();
        tab.Buffer.GetSelectionBounds(out var selStart, out var selEnd);
        var start = TextOffsets.CharToUtf16(text, selStart.GetOffset());
        var end = TextOffsets.CharToUtf16(text, selEnd.GetOffset());
        Status("Looking for fixes and refactorings…", 20);
        IReadOnlyList<CodeActionItem> items;
        try { items = await Task.Run(() => codeActions.GetActionsAsync(tab.Path, text, start, end, CancellationToken.None)); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Code actions failed");
            Status("Code actions failed: " + e.Message);
            return;
        }
        if (items.Count == 0)
        {
            Status("No quick fixes or refactorings here.");
            return;
        }
        Status("");
        actionMenu ??= new ActionMenu();
        actionMenu.Show(tab.View, tab.CaretRectangle(), items.Select(i => (i.Title, i.Fixes)).ToList(), index => _ = ApplyCodeActionAsync(items[index]));
    }

    /// <summary>Ctrl+I anywhere in a class: one interface / abstract class to implement → done; several → a menu.</summary>
    async Task ImplementMissingMembersAsync()
    {
        if (editors.Active is not { } tab || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var open in editors.Tabs.Where(t => t.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            session.Workspace.UpdateText(open.Path, open.GetText());
        var text = tab.GetText();
        var position = TextOffsets.CharToUtf16(text, tab.CursorOffset);
        Status("Looking for members to implement…", 20);
        IReadOnlyList<CodeActionItem> items;
        try { items = await Task.Run(() => codeActions.GetImplementMembersAsync(tab.Path, text, position, CancellationToken.None)); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Implement members failed");
            Status("Implement members failed: " + e.Message);
            return;
        }
        if (items.Count == 0)
        {
            Status("Nothing to implement: the class has all interface and abstract members.");
            return;
        }
        var plain = items.Where(i => i.Title is "Implement interface" or "Implement abstract class").ToList();
        if (plain.Count == 1)
        {
            await ApplyCodeActionAsync(plain[0]);
            return;
        }
        Status("");
        actionMenu ??= new ActionMenu();
        actionMenu.Show(tab.View, tab.CaretRectangle(), items.Select(i => (i.Title, i.Fixes)).ToList(), index => _ = ApplyCodeActionAsync(items[index]));
    }

    async Task ApplyCodeActionAsync(CodeActionItem item)
    {
        Status($"{item.Title}…", 20);
        try
        {
            var edits = await Task.Run(() => codeActions.ApplyAsync(item.Id, CancellationToken.None));
            if (edits.Count == 0)
            {
                Status("The action changed nothing.");
                return;
            }
            var result = ApplyFileEdits(edits);
            editors.RefreshAnalysis();
            if (result.Created > 0) _ = session.ReloadProjectsAsync(); // new documents
            await ReportSkippedAsync($"{item.Title}: {result.InEditor + result.OnDisk + result.Created} file(s) changed"
                + (result.OnDisk + result.Created > 0 ? $" ({result.OnDisk + result.Created} on disk)" : "") + ".", result.Skipped);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Code action failed");
            await Ui.ShowErrorAsync(window, "The action failed", e.Message);
        }
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---------- NuGet ----------

    void OpenPackages(string? project)
    {
        if (!session.IsOpen) return;
        packagesWindow ??= new PackagesWindow(window, packages, AllProjects, bottom.AppendOutput, changedProject =>
        {
            // References changed: Roslyn must read the projects again; Directory.Packages.props may have changed too.
            _ = session.ReloadProjectsAsync();
            explorer.RefreshPaths([changedProject]);
            Status($"Packages of {Path.GetFileNameWithoutExtension(changedProject)} changed; reloading projects…");
        }, () => session.Root?.Solution?.Path ?? session.Folder, path => editors.Open(path));
        packagesWindow.Open(project ?? CurrentProject);
    }

    IReadOnlyList<string> AllProjects()
    {
        if (session.Root is not { } root) return [];
        var list = root.Solution?.Projects.Select(p => p.Path) ?? root.LooseProjects;
        return list.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).ToList();
    }
}

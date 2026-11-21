using LightweightIDE.App.Views;
using LightweightIDE.Indexing;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>Go to Implementation, recent files (Ctrl+E) and file structure (Ctrl+F12).</summary>
public sealed partial class MainWindow
{
    const int MaxRecentFiles = 50;

    void InitNavigation()
    {
        editors.ActiveTabChanged += tab =>
        {
            if (tab is null || session.Folder is not { } folder) return;
            var list = state.RecentFiles.TryGetValue(folder, out var l) ? l : state.RecentFiles[folder] = [];
            list.Remove(tab.Path);
            list.Insert(0, tab.Path);
            if (list.Count > MaxRecentFiles) list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);
        };
    }

    void ShowRecentFiles()
    {
        if (session.Folder is not { } folder) return;
        var recent = (state.RecentFiles.GetValueOrDefault(folder) ?? []).Where(File.Exists).ToList();
        // The active file is first in the list; Ctrl+E then Enter switches to the previous one, like Rider.
        var active = editors.Active?.Path;
        if (active is not null && recent.FirstOrDefault() == active) recent = [.. recent.Skip(1), active];
        palette.Open(PaletteMode.Recent, (query, _) =>
        {
            var rows = recent.Select(p => (Path: p, Score: query.Length == 0 ? 0 : FuzzyMatcher.Score(query, Path.GetFileName(p)) ?? FuzzyMatcher.Score(query, p)))
                .Where(r => r.Score is not null);
            if (query.Length > 0) rows = rows.OrderByDescending(r => r.Score);
            return rows.Select(r => (new Location(r.Path, 0, 1),
                $"{Path.GetFileName(r.Path)}\t{(r.Path.StartsWith(folder, StringComparison.Ordinal) ? Path.GetRelativePath(folder, r.Path) : r.Path)}")).ToList();
        }, "Recent files");
    }

    void ShowFileStructure()
    {
        if (editors.Active is not { } tab || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        var symbols = SymbolExtractor.Extract(tab.GetText()).Where(s => s.Kind != "namespace").ToList();
        palette.Open(PaletteMode.Structure, (query, _) =>
        {
            var rows = symbols.Select(s => (Symbol: s, Score: query.Length == 0 ? 0 : FuzzyMatcher.Score(query, s.Name))).Where(r => r.Score is not null);
            if (query.Length > 0) rows = rows.OrderByDescending(r => r.Score);
            return rows.Select(r => (new Location(tab.Path, r.Symbol.Line, r.Symbol.Column, r.Symbol.Name.Length),
                $"{Indent(r.Symbol)}{r.Symbol.Name}\t{r.Symbol.Kind}  :{r.Symbol.Line}")).ToList();
        }, $"Structure of {tab.Document.FileName}");

        static string Indent(IndexedSymbol s) => s.Container is null ? "" : new string(' ', 2 * s.Container.Count(c => c == '.') + 2);
    }

    async Task GoToImplementationAsync()
    {
        if (NavigationContext() is not { } c) return;
        Status("Finding implementations…");
        try
        {
            var result = await Task.Run(() => navigation.FindImplementationsAsync(c.Tab.Path, c.Text, c.Position, c.Token), c.Token);
            if (result is null) Status("No symbol under the caret.");
            else if (result.Locations.Count == 0) Status($"No implementations of {result.SymbolDisplay} in the solution.");
            else if (result.Locations.Count == 1)
            {
                var l = result.Locations[0];
                Navigate(new Location(l.Path, l.Line, l.Column, l.Length));
                Status("");
            }
            else
            {
                ShowPanel(true);
                bottom.SetResults($"Implementations of {result.SymbolDisplay}: {result.Locations.Count}", ToResults(result.Locations));
                Status("");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogWarning(e, "Go to implementation failed");
            Status("Go to implementation failed: " + e.Message);
        }
    }

    LightweightIDE.Language.Navigation.TestNavigation? testNavigation;

    /// <summary>
    /// Ctrl+Shift+G: from a class or method to its tests (named after it, or using it), from a test to the tested
    /// class. One result opens; several are listed in Find Results.
    /// </summary>
    async Task GoToTestsAsync()
    {
        if (NavigationContext() is not { } c) return;
        testNavigation ??= new LightweightIDE.Language.Navigation.TestNavigation(session.Workspace);
        Status("Finding tests…", 30);
        try
        {
            var result = await Task.Run(() => testNavigation.FindAsync(c.Tab.Path, c.Text, c.Position, c.Token), c.Token);
            if (result is null)
            {
                Status("Place the caret in a class or a method.");
                return;
            }
            if (result.Targets.Count == 0)
            {
                Status(result.ToTests ? $"No tests found for {result.Subject} (by name or by use in test projects)."
                    : $"No tested class found for {result.Subject}.", 10);
                return;
            }
            if (result.Targets.Count == 1)
            {
                var t = result.Targets[0];
                Navigate(new Location(t.Location.Path, t.Location.Line, t.Location.Column, t.Location.Length));
                Status($"{t.Name}: {t.Reason}", 8);
                return;
            }
            ShowPanel(true);
            var title = result.ToTests ? $"Tests of {result.Subject}: {result.Targets.Count}" : $"Tested by {result.Subject}: {result.Targets.Count}";
            bottom.SetResults(title, result.Targets
                .Select(t => (new Location(t.Location.Path, t.Location.Line, t.Location.Column, t.Location.Length), $"{t.Name}  —  {t.Reason}"))
                .ToList());
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogWarning(e, "Go to tests failed");
            Status("Go to tests failed: " + e.Message);
        }
    }
}

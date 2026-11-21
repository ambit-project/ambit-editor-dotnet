using LightweightIDE.App.Application;
using LightweightIDE.App.Views;
using LightweightIDE.Editor;
using LightweightIDE.Language.Formatting;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>Unsaved buffers are copied to disk every few seconds and offered back after a crash or a hard reset.</summary>
public sealed partial class MainWindow
{
    const int BackupIntervalMs = 5000;
    readonly UnsavedBackupStore backups = new(Path.Combine(AppPaths.StateDirectory, "backup"));
    readonly Dictionary<string, int> backedUpVersions = new(StringComparer.Ordinal);

    void InitRecovery()
    {
        Ui.Every(BackupIntervalMs, () =>
        {
            BackupDirtyTabs();
            return true;
        });
        // Saved, or closed after the user chose not to save: the backup is no longer needed.
        editors.TabSaved += tab => ForgetBackup(tab.Path);
        editors.TabClosed += tab => ForgetBackup(tab.Path);
    }

    void BackupDirtyTabs()
    {
        foreach (var tab in editors.Tabs)
        {
            if (!tab.IsDirty)
            {
                if (backedUpVersions.ContainsKey(tab.Path)) ForgetBackup(tab.Path);
                continue;
            }
            if (backedUpVersions.TryGetValue(tab.Path, out var version) && version == tab.Document.Version) continue;
            backedUpVersions[tab.Path] = tab.Document.Version;
            var backup = new UnsavedBackup(tab.Path, tab.GetText(), DateTime.UtcNow, tab.Document.DiskLastWriteUtc);
            _ = Task.Run(() =>
            {
                try { backups.Save(backup); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Cannot back up {Path}", backup.Path); }
            });
        }
    }

    void ForgetBackup(string path)
    {
        backedUpVersions.Remove(path);
        _ = Task.Run(() => backups.Delete(path));
    }

    /// <summary>Normal exit: every buffer was saved or discarded, so this session's backups go away.</summary>
    void DeleteSessionBackups()
    {
        foreach (var path in backedUpVersions.Keys.ToList()) backups.Delete(path);
        backedUpVersions.Clear();
    }

    /// <summary>After a folder opens: offers the backups left by a crash for files in it.</summary>
    async Task OfferRecoveryAsync(string folder)
    {
        var found = backups.List(folder).Where(b => !backedUpVersions.ContainsKey(b.Path)).ToList();
        if (found.Count == 0) return;
        string Relative(string p) => p.StartsWith(folder, StringComparison.Ordinal) ? Path.GetRelativePath(folder, p) : p;
        var lines = found.Take(12).Select(b => $"  {Relative(b.Path)}  ({b.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm})").ToList();
        if (found.Count > 12) lines.Add($"  … and {found.Count - 12} more");
        var choice = await Ui.ChooseAsync(window, $"Recover unsaved changes in {found.Count} file(s)?",
            "The IDE did not close normally. These edits were never saved:\n" + string.Join("\n", lines) +
            "\n\nRecover opens the files with these edits (unsaved, Ctrl+Z restores the file on disk).",
            ["Discard", "Later", "Recover"], 1, 2);
        switch (choice)
        {
            case 0:
                foreach (var b in found) backups.Delete(b.Path);
                Status($"Discarded {found.Count} backup(s).");
                break;
            case 2:
                var recovered = 0;
                var missing = new List<string>();
                foreach (var backup in found)
                {
                    if (!File.Exists(backup.Path))
                    {
                        missing.Add(backup.Path);
                        continue;
                    }
                    if (editors.Open(backup.Path, focus: false) is not { } tab) continue;
                    var current = tab.GetText();
                    if (current != backup.Text && tab.ApplyEdits(current, TextDiff.Compute(current, backup.Text))) recovered++;
                    if (backup.DiskLastWriteUtc is { } then && tab.Document.DiskLastWriteUtc > then.AddSeconds(1))
                        Status($"{Path.GetFileName(backup.Path)} changed on disk after the backup; compare before saving.", 15);
                }
                if (missing.Count > 0)
                    await Ui.ShowErrorAsync(window, "Some files no longer exist",
                        "Their backups are kept in " + backups.Directory + ":\n" + string.Join("\n", missing.Select(Relative)));
                if (recovered > 0) Status($"Recovered unsaved changes in {recovered} file(s). Save them with Ctrl+S.", 12);
                break;
        }
    }
}

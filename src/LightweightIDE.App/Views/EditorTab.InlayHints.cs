using LightweightIDE.Language.Classification;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Views;

/// <summary>
/// Inlay hints (parameter names, inferred types). GtkSourceView 5 has no virtual text, and inserting anything into the
/// buffer would shift every offset, so a hint is drawn without touching the text:
/// a neighbouring character (a space or punctuation, the "anchor") gets extra letter spacing — the room for the hint —
/// and becomes transparent; an overlay label in buffer coordinates draws the anchor character again together with the
/// hint. Tags and marks move with edits, the labels follow them; the buffer, undo and saving never see a hint.
/// </summary>
public sealed partial class EditorTab
{
    sealed record HintView(string Key, Gtk.TextTag Tag, Gtk.TextMark Mark, Gtk.Label Label, char Anchor);

    readonly List<HintView> hintViews = [];
    uint hintTimer;
    uint hintLayoutTimer;
    CancellationTokenSource? hintCts;
    int hintSerial;
    bool inlayHintsOn;

    bool InlayHintsEnabled => isCSharp && !isLarge && inlayHintsOn;

    /// <summary>View → Inlay Hints: turns the hints on or off for this editor.</summary>
    public void SetInlayHints(bool on)
    {
        inlayHintsOn = on;
        if (InlayHintsEnabled) ScheduleHints(10);
        else ClearHints();
    }

    void ScheduleHints(int delay)
    {
        if (!InlayHintsEnabled) return;
        Ui.Cancel(ref hintTimer);
        hintTimer = Ui.After(delay, () =>
        {
            hintTimer = 0;
            _ = UpdateHintsAsync();
        });
    }

    /// <summary>After an edit the tags have moved with the text; move the labels to them once the layout is valid.</summary>
    void ScheduleHintLayout()
    {
        if (hintViews.Count == 0) return;
        Ui.Cancel(ref hintLayoutTimer);
        hintLayoutTimer = Ui.After(1, () =>
        {
            hintLayoutTimer = 0;
            PositionHints();
        });
    }

    async Task UpdateHintsAsync()
    {
        hintCts?.Cancel();
        var cts = hintCts = new CancellationTokenSource();
        View.GetVisibleRect(out var rect);
        int firstLine, lastLine;
        if (rect.Height <= 0)
        {
            firstLine = 0;
            lastLine = 200;
        }
        else
        {
            View.GetLineAtY(out var top, rect.Y, out _);
            View.GetLineAtY(out var bottom, rect.Y + rect.Height, out _);
            firstLine = top.GetLine();
            lastLine = bottom.GetLine();
        }
        firstLine = Math.Max(0, firstLine - 40);
        lastLine = Math.Min(Buffer.GetLineCount() - 1, lastLine + 40);
        Buffer.GetIterAtLine(out var from, firstLine);
        Buffer.GetIterAtLine(out var to, lastLine);
        if (!to.EndsLine()) to.ForwardToLineEnd();
        var fromChar = from.GetOffset();
        var toChar = to.GetOffset();
        var text = GetText();
        var version = Document.Version;
        var path = Document.Path;
        IReadOnlyList<InlayHint> hints;
        try
        {
            hints = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.InlayHints.GetHintsAsync(path, text, TextOffsets.CharToUtf16(text, fromChar), TextOffsets.CharToUtf16(text, toChar), cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            services.Logger.LogDebug(e, "Inlay hints failed");
            return;
        }
        if (cts.IsCancellationRequested || version != Document.Version || !InlayHintsEnabled) return;
        ApplyHints(text, hints);
    }

    void ApplyHints(string text, IReadOnlyList<InlayHint> hints)
    {
        var map = CharMap(text);
        var wanted = new Dictionary<string, (int Anchor, bool AnchorFirst, InlayHint Hint)>(StringComparer.Ordinal);
        foreach (var hint in hints)
        {
            var position = map is null ? hint.Position : map[Math.Clamp(hint.Position, 0, text.Length)];
            if (ChooseAnchor(position, hint.Kind) is not { } anchor) continue;
            var key = $"{anchor.Offset}|{anchor.AnchorFirst}|{hint.Text}";
            wanted.TryAdd(key, (anchor.Offset, anchor.AnchorFirst, hint));
        }
        // Keep what is still wanted at the same place; remove the rest; add the new ones.
        foreach (var view in hintViews.ToList())
        {
            Buffer.GetIterAtMark(out var at, view.Mark);
            var key = view.Key[(view.Key.IndexOf('|'))..];
            if (wanted.Remove(at.GetOffset() + key)) continue;
            RemoveHint(view);
        }
        foreach (var (key, (offset, anchorFirst, hint)) in wanted) AddHint(key, offset, anchorFirst, hint);
        ScheduleHintLayout();
    }

    /// <summary>
    /// The character that makes room for a hint at <paramref name="position"/> (a character offset). The hint sits
    /// between position - 1 and position; a whitespace or punctuation neighbour is redrawn without changing how
    /// the code looks. Null when only identifier characters or a line break are around.
    /// </summary>
    (int Offset, bool AnchorFirst)? ChooseAnchor(int position, InlayHintKind kind)
    {
        Buffer.GetIterAtOffset(out var after, position);
        var next = after.IsEnd() ? '\n' : (char)after.GetChar();
        var previous = '\n';
        if (position > 0)
        {
            Buffer.GetIterAtOffset(out var before, position - 1);
            previous = (char)before.GetChar();
        }
        static bool Usable(char c) => c is not '\n' and not '\r' and not '\t' && !char.IsLetterOrDigit(c) && c != '_' && c != '"' && c != '\'' && c != '@' && c != '$';
        // Parameter and lambda hints prefer the character before ("(" or " "), type hints the one after (" ", ")", ";").
        if (kind == InlayHintKind.Type && Usable(next)) return (position, false);
        if (Usable(previous)) return (position - 1, true);
        if (Usable(next)) return (position, false);
        return null;
    }

    void AddHint(string key, int offset, bool anchorFirst, InlayHint hint)
    {
        Buffer.GetIterAtOffset(out var start, offset);
        var end = start.Copy();
        end.ForwardChar();
        var anchor = (char)start.GetChar();

        var family = GLib.Functions.MarkupEscapeText(services.Options.FontFamily.Replace("\"", ""), -1);
        var size = services.Options.FontSize * 1024;
        var small = (int)(size * 0.82);
        var anchorMarkup = $"<span font_family=\"{family}\" size=\"{size}\" foreground=\"{AnchorColor()}\">{GLib.Functions.MarkupEscapeText(anchor.ToString(), -1)}</span>";
        var hintText = hint.Kind == InlayHintKind.Type && !hint.Text.StartsWith(':') ? hint.Text + " " : hint.Text;
        var spacer = hint.Kind == InlayHintKind.Parameter || !hint.Text.StartsWith(':') ? " " : "";
        var hintMarkup = $"<span font_family=\"{family}\" size=\"{small}\" foreground=\"#8a8f98\" background=\"#8a8f98\" bgalpha=\"14%\"> {GLib.Functions.MarkupEscapeText(hintText.TrimEnd(), -1)} </span>" +
                         $"<span font_family=\"{family}\" size=\"{small}\">{spacer}</span>";
        var markup = anchorFirst ? anchorMarkup + hintMarkup : hintMarkup + anchorMarkup;

        // Room for the hint = label width - the anchor's own width.
        var layout = View.CreatePangoLayout(null);
        layout.SetMarkup(markup, -1);
        layout.GetPixelSize(out var labelWidth, out _);
        layout.SetMarkup(anchorMarkup, -1);
        layout.GetPixelSize(out var anchorWidth, out _);
        var room = Math.Max(0, labelWidth - anchorWidth);

        var tag = Gtk.TextTag.New($"inlay-{++hintSerial}");
        tag.LetterSpacing = room * 1024;
        // The label draws the anchor; the real glyph (in the middle of the room) must not show. Alpha 0 does not work:
        // GTK passes it to Pango as foreground-alpha, where 0 means "unset" (opaque); the smallest value does.
        tag.Foreground = "rgba(0,0,0,0.0001)";
        Buffer.TagTable!.Add(tag);
        tag.SetPriority(Buffer.TagTable.GetSize() - 1); // above semantic colors, which would make the anchor visible
        Buffer.ApplyTag(tag, start, end);

        var label = Gtk.Label.New(null);
        label.SetMarkup(markup);
        label.CanTarget = false; // clicks go to the text
        label.CanFocus = false;
        label.AddCssClass("inlay-hint");
        View.AddOverlay(label, 0, 0);
        label.Visible = false;
        var mark = Buffer.CreateMark(null, start, true);
        hintViews.Add(new HintView(offset + key[key.IndexOf('|')..], tag, mark, label, anchor));
    }

    /// <summary>The scheme's text color, so a redrawn anchor looks like the rest of the code.</summary>
    string AnchorColor()
    {
        var style = Buffer.StyleScheme?.GetStyle("text");
        var color = style is null ? null : GetStyleColor(style, "foreground", "foreground-set");
        return color ?? (Ui.IsDarkTheme() ? "#d4d4d4" : "#1e1e1e");

        static string? GetStyleColor(GtkSource.Style style, string property, string setProperty)
        {
            var set = new GObject.Value(GObject.Type.Boolean);
            style.GetProperty(setProperty, set);
            if (!set.GetBoolean()) return null;
            var value = new GObject.Value(GObject.Type.String);
            style.GetProperty(property, value);
            return value.GetString();
        }
    }

    /// <summary>Moves each label onto its anchor (the tag has already made room); hides it if the anchor was edited away.</summary>
    void PositionHints()
    {
        foreach (var view in hintViews.ToList())
        {
            if (view.Mark.GetDeleted())
            {
                RemoveHint(view);
                continue;
            }
            Buffer.GetIterAtMark(out var at, view.Mark);
            var end = at.Copy();
            end.ForwardChar();
            if (at.IsEnd() || (char)at.GetChar() != view.Anchor || !at.HasTag(view.Tag))
            {
                RemoveHint(view);
                continue;
            }
            View.GetIterLocation(at, out var rect);
            View.MoveOverlay(view.Label, rect.X, rect.Y);
            view.Label.Visible = true;
        }
    }

    void RemoveHint(HintView view)
    {
        Buffer.GetStartIter(out var start);
        Buffer.GetEndIter(out var end);
        Buffer.RemoveTag(view.Tag, start, end);
        Buffer.TagTable!.Remove(view.Tag);
        if (!view.Mark.GetDeleted()) Buffer.DeleteMark(view.Mark);
        view.Label.Visible = false; // removing an overlay child alone does not repaint its area
        View.Remove(view.Label);
        hintViews.Remove(view);
        View.QueueDraw();
    }

    void ClearHints()
    {
        hintCts?.Cancel();
        Ui.Cancel(ref hintTimer);
        foreach (var view in hintViews.ToList()) RemoveHint(view);
    }
}

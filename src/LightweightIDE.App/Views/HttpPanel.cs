using LightweightIDE.DotNet.Http;

namespace LightweightIDE.App.Views;

/// <summary>Responses of .http requests: history (newest first) and the selected one as body / headers / request.</summary>
public sealed class HttpPanel
{
    const int MaxHistory = 30;
    readonly Gtk.Paned root = Gtk.Paned.New(Gtk.Orientation.Horizontal);
    readonly Gtk.ListBox history = Gtk.ListBox.New();
    readonly Gtk.Label summary = Ui.Label("Send a request from a .http file: the ▶ in the gutter or Ctrl+Enter.", "dim-label");
    readonly GtkSource.Buffer body = GtkSource.Buffer.New(null);
    readonly Gtk.TextView headers = Gtk.TextView.New();
    readonly Gtk.TextView request = Gtk.TextView.New();
    readonly Gtk.Stack pages = Gtk.Stack.New();
    readonly List<HttpExchange> exchanges = [];

    public Gtk.Widget Widget => root;

    public HttpPanel()
    {
        var left = Ui.VBox();
        left.Append(Ui.Label("HISTORY", "panel-title"));
        history.AddCssClass("result-list");
        history.SelectionMode = Gtk.SelectionMode.Single;
        history.OnRowSelected += (_, e) =>
        {
            if (e.Row is not null && exchanges.ElementAtOrDefault(e.Row.GetIndex()) is { } x) Show(x);
        };
        left.Append(Ui.Scrolled(history));
        root.SetStartChild(left);
        root.Position = 300;

        var right = Ui.VBox();
        var bar = Ui.HBox(8);
        bar.AddCssClass("panel-toolbar");
        summary.Hexpand = true;
        summary.Ellipsize = Pango.EllipsizeMode.End;
        summary.Selectable = true;
        bar.Append(summary);
        var switcher = Gtk.StackSwitcher.New();
        switcher.Stack = pages;
        bar.Append(switcher);
        right.Append(bar);

        body.Language = GtkSource.LanguageManager.GetDefault().GetLanguage("json");
        body.HighlightSyntax = true;
        var bodyView = GtkSource.View.NewWithBuffer(body);
        foreach (var view in new Gtk.TextView[] { bodyView, headers, request })
        {
            view.Editable = false;
            view.Monospace = true;
            view.WrapMode = Gtk.WrapMode.WordChar;
            view.LeftMargin = 8;
        }
        bodyView.ShowLineNumbers = true;
        pages.AddTitled(Ui.Scrolled(bodyView), "body", "Body");
        pages.AddTitled(Ui.Scrolled(headers), "headers", "Headers");
        pages.AddTitled(Ui.Scrolled(request), "request", "Request");
        pages.Vexpand = true;
        right.Append(pages);
        root.SetEndChild(right);
    }

    public void SetStyleScheme(GtkSource.StyleScheme? scheme) => body.StyleScheme = scheme;

    public void ShowSending(string title) => summary.SetText($"Sending {title}…");

    public void Add(HttpExchange exchange)
    {
        exchanges.Insert(0, exchange);
        if (exchanges.Count > MaxHistory) exchanges.RemoveAt(exchanges.Count - 1);
        var row = Ui.HBox(8);
        var status = Ui.Label(exchange.Status?.ToString() ?? "ERR", exchange.Status is >= 200 and < 400 ? "test-passed" : "test-failed");
        row.Append(status);
        var title = Ui.Label($"{exchange.Method} {exchange.Title}");
        title.Hexpand = true;
        title.Ellipsize = Pango.EllipsizeMode.End;
        title.TooltipText = exchange.Url;
        row.Append(title);
        row.Append(Ui.Label($"{exchange.Elapsed.TotalMilliseconds:0} ms", "dim-label"));
        history.Insert(row, 0);
        while (history.GetRowAtIndex(MaxHistory) is { } extra) history.Remove(extra);
        if (history.GetRowAtIndex(0) is { } first) history.SelectRow(first);
        Show(exchange);
    }

    void Show(HttpExchange x)
    {
        summary.SetText(x.Error is not null
            ? $"{x.Method} {x.Url} — {x.Error}"
            : $"{x.Status} {x.Reason} · {x.Elapsed.TotalMilliseconds:0} ms · {Size(x.Size)} · {x.ContentType ?? "no content type"} · {x.At:HH:mm:ss}");
        summary.TooltipText = x.Url;
        var text = x.Error ?? (x.ResponseBody is { Length: > 0 } b ? (x.IsJson ? HttpClientService.Pretty(b) : b) : "(empty body)");
        body.SetText(text, -1);
        headers.Buffer!.SetText(string.Join("\n", x.ResponseHeaders.Select(h => $"{h.Name}: {h.Value}")), -1);
        request.Buffer!.SetText($"{x.Method} {x.Url}\n{string.Join("\n", x.RequestHeaders.Select(h => $"{h.Name}: {h.Value}"))}\n\n{x.RequestBody}", -1);
        pages.VisibleChildName = "body";
    }

    static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1048576.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}

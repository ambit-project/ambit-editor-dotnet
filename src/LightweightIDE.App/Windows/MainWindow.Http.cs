using LightweightIDE.App.Views;
using LightweightIDE.DotNet.Http;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>.http files: send the request at the caret or next to the ▶ and show the response in the HTTP panel.</summary>
public sealed partial class MainWindow
{
    CancellationTokenSource? httpCts;

    void InitHttp() => editors.HttpSendRequested += (tab, line) => _ = SendHttpAsync(tab, line);

    async Task SendHttpAsync(EditorTab tab, int line)
    {
        httpCts?.Cancel();
        var cts = httpCts = new CancellationTokenSource();
        var text = tab.GetText();
        var request = HttpFile.Parse(text).RequestAt(line);
        if (request is null)
        {
            Status("Place the caret inside a request (METHOD URL), or click the ▶ next to it.");
            return;
        }
        ShowPage(BottomPanel.HttpPage);
        bottom.Http.ShowSending(request.Title);
        Status($"Sending {request.Method} {request.Title}…", 30);
        try
        {
            var exchange = await Task.Run(() => http.SendAsync(tab.Path, text, line, cts.Token), cts.Token);
            bottom.Http.Add(exchange);
            Status(exchange.Error is not null ? $"{request.Title}: {exchange.Error}"
                : $"{request.Title}: {exchange.Status} {exchange.Reason} in {exchange.Elapsed.TotalMilliseconds:0} ms", 10);
        }
        catch (HttpVariableException e)
        {
            Status(e.Message, 15);
            bottom.Http.ShowSending("");
            await Ui.ShowErrorAsync(window, "Cannot send the request", e.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogWarning(e, "HTTP request failed");
            Status("Request failed: " + e.Message, 15);
        }
    }
}

using LightweightIDE.App.Windows;
using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.Formatting;
using LightweightIDE.DotNet.Http;
using LightweightIDE.DotNet.Packages;
using LightweightIDE.DotNet.Scaffolding;
using LightweightIDE.DotNet.Testing;
using LightweightIDE.Editor;
using LightweightIDE.Indexing;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.Diagnostics;
using LightweightIDE.Language.Formatting;
using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.Refactoring;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LightweightIDE.App.Application;

/// <summary>Composition root: configuration, logging, services and the GTK application.</summary>
public sealed class AppHost : IDisposable
{
    readonly ServiceProvider services;

    AppHost(ServiceProvider services) => this.services = services;

    public static AppHost Create()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(AppPaths.UserSettingsFile, optional: true, reloadOnChange: false)
            .Build();

        Directory.CreateDirectory(AppPaths.LogDirectory);
        var level = Enum.TryParse<LogEventLevel>(configuration["Serilog:MinimumLevel"], out var l) ? l : LogEventLevel.Information;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            // Several IDE windows write one daily file: AppendFileSink appends each line with O_APPEND.
            .WriteTo.Sink(new AppendFileSink(AppPaths.LogDirectory, new Serilog.Formatting.Display.MessageTemplateTextFormatter(
                "{Timestamp:HH:mm:ss.fff} {Pid} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"), retainedDays: 7))
            .CreateLogger();

        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddLogging(b => b.ClearProviders().AddSerilog(dispose: true));
        collection.AddSingleton(Bind<UiOptions>(configuration, "Ui"));
        collection.AddSingleton(Bind<EditorOptions>(configuration, "Editor"));
        collection.AddSingleton(Bind<LanguageOptions>(configuration, "Language"));
        collection.AddSingleton(Bind<IndexOptions>(configuration, "Indexing"));
        collection.AddSingleton(Bind<FormattingOptions>(configuration, "Formatting"));
        collection.AddSingleton(AppState.Load());
        collection.AddSingleton<BuildService>();
        collection.AddSingleton<TestRunner>();
        collection.AddSingleton<WorkspaceService>();
        collection.AddSingleton<DiagnosticsService>();
        collection.AddSingleton<NavigationService>();
        collection.AddSingleton<LightweightIDE.Language.Analysis.CodeModel>();
        collection.AddSingleton<CompletionProvider>();
        collection.AddSingleton<LightweightIDE.Language.Classification.SemanticHighlighter>();
        collection.AddSingleton<LightweightIDE.Language.Classification.InlayHintService>();
        collection.AddSingleton<LightweightIDE.Language.Classification.SymbolUsages>();
        collection.AddSingleton<LightweightIDE.Language.Classification.UnnecessaryCode>();
        collection.AddSingleton<SignatureHelpProvider>();
        collection.AddSingleton<RenameService>();
        collection.AddSingleton<CodeActionService>();
        collection.AddSingleton<CSharpierClient>();
        collection.AddSingleton<FormattingService>();
        collection.AddSingleton<PackageService>();
        collection.AddSingleton<SolutionScaffolder>();
        collection.AddSingleton<HttpClientService>();
        collection.AddSingleton<SolutionSession>();
        collection.AddSingleton<MainWindowFactory>();
        return new AppHost(collection.BuildServiceProvider());
    }

    static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var value = new T();
        configuration.GetSection(section).Bind(value);
        return value;
    }

    public int Run(string? startPath)
    {
        var logger = services.GetRequiredService<ILogger<AppHost>>();
        logger.LogInformation("Starting Lightweight IDE {AppVersion} on .NET {Version}", AppVersion.Display, Environment.Version);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        GtkSource.Module.Initialize();
        GtkSource.Functions.Init();

        var app = Gtk.Application.New("dev.ambit.LightweightIDE", Gio.ApplicationFlags.NonUnique);
        app.OnActivate += (_, _) =>
        {
            var window = services.GetRequiredService<MainWindowFactory>().Create(app);
            window.Present(startPath);
        };
        var code = app.RunWithSynchronizationContext(null);
        services.GetRequiredService<AppState>().Save();
        logger.LogInformation("Exit {Code}", code);
        return code;
    }

    public void Dispose()
    {
        services.Dispose();
        Log.CloseAndFlush();
    }
}

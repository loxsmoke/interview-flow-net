using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels;
using InterviewFlow.App.Views;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Logging;

namespace InterviewFlow.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Hand-rolled composition root (no DI container — docs/01-architecture.md):
        // load config first so every store below sees the resolved data dir.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = AppConfig.Load();
            DiagnosticLog.Info("app", $"starting; env={config.Env.Path}; data={config.DataDir()}");
            // OTel export when OTEL_EXPORTER_OTLP_ENDPOINT is set (docs/05 §5.8).
            Telemetry.Initialize(
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(config),
            };
            desktop.Exit += (_, _) =>
            {
                Telemetry.Shutdown();
                DiagnosticLog.Shutdown("clean exit");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

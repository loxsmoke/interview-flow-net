using Avalonia;

namespace InterviewFlow.App;

internal static class Program
{
    // Avalonia configuration is also used by the visual designer — keep
    // BuildAvaloniaApp free of side effects.
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort crash capture: a stack trace in the diagnostic log beats a
        // window that vanishes with no trace of why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Logging.DiagnosticLog.Error("crash", "unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Core.Logging.DiagnosticLog.Error("crash", "unobserved task exception", e.Exception);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Core.Logging.DiagnosticLog.Error("crash", "fatal startup/runtime error", ex);
            Core.Logging.DiagnosticLog.Shutdown("crash");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Inter is bundled so typography is identical on Windows and macOS
            // (fidelity rule: docs/00-overview.md).
            .WithInterFont()
            .LogToTrace();
}

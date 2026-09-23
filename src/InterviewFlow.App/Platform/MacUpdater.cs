using System.Runtime.InteropServices;
using Avalonia.Threading;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Logging;

namespace InterviewFlow.App.Platform;

/// <summary>Sparkle is bundled only by publish-macos.sh. No native calls on Windows.</summary>
internal static class MacUpdater
{
    private static bool _attempted;
    public static bool IsAvailable { get; private set; }
    public static string Status { get; private set; } = "Updates are available in the installed macOS release.";

    public static void Initialize(AppConfig config)
    {
        if (!OperatingSystem.IsMacOS() || _attempted)
            return;
        Dispatcher.UIThread.VerifyAccess();
        _attempted = true;
        var executableDir = new DirectoryInfo(AppContext.BaseDirectory);
        var bundle = executableDir.Parent?.Parent;
        if (executableDir.Name != "MacOS" || executableDir.Parent?.Name != "Contents" ||
            bundle is null || !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return;

        // Replacing the bundle must never replace someone's configuration or data.
        if (IsInsideBundle(config.Env.Path, bundle.FullName) || IsInsideBundle(config.DataDir(), bundle.FullName))
        {
            Status = "Move your settings and data outside the app bundle before updating.";
            return;
        }

        try
        {
            IsAvailable = Start() == 1;
            Status = IsAvailable ? "Updates are downloaded and installed by Sparkle." :
                "The updater could not start. See the macOS Console log for details.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Status = "This build does not include the macOS updater. Install an updater-enabled release.";
            DiagnosticLog.Error("updater", "Could not load Sparkle bridge", ex);
        }
    }

    internal static bool IsInsideBundle(string path, string bundle) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(bundle).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(bundle).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void CheckForUpdates()
    {
        if (!OperatingSystem.IsMacOS() || !IsAvailable)
            return;
        Dispatcher.UIThread.VerifyAccess();
        Check();
    }

    [DllImport("InterviewFlow.Updater", EntryPoint = "if_updater_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Start();

    [DllImport("InterviewFlow.Updater", EntryPoint = "if_updater_check", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Check();
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewFlow.App.Platform;

/// <summary>
/// Cross-platform "open externally" helpers (docs/01-architecture.md). Only
/// http/https URLs ever leave the app (§4.3); failures are swallowed like the
/// original — no browser is not our problem to surface.
/// </summary>
public static class ShellOpen
{
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", uri.ToString());
            else
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch
        {
            // No browser / blocked — ignore, matching the original.
        }
    }

    /// <summary>Reveal a file in Explorer / Finder.</summary>
    public static void RevealInFileManager(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", ["-R", path]);
            else
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch
        {
            // Ignore, matching the original.
        }
    }
}

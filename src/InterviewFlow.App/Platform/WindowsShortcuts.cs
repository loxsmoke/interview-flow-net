using System.Reflection;

namespace InterviewFlow.App.Platform;

/// <summary>Where a Windows shortcut to the app can live (docs/03 §3.11).</summary>
public enum ShortcutPlace
{
    Desktop,
    StartMenu,
}

/// <summary>
/// Shortcuts to the running executable on Windows: the desktop and the Start
/// menu get a <c>.lnk</c> written by <see cref="ShellLink"/>. The taskbar is
/// deliberately not offered: Windows 11 ignores the shell's hidden pin verb
/// from any process but Explorer, and the supported pin API needs package
/// identity, which a portable zip has not got. "Same location" throughout
/// means a link whose target is this executable's path, so a shortcut to
/// another copy of the app is left alone.
/// </summary>
public static class WindowsShortcuts
{
    public const string ShortcutName = "Interview Flow";

    /// <summary>True on Windows when the running executable can be located.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() && TargetExe() is not null;

    /// <summary>
    /// The executable a shortcut should launch: the apphost, or null when the
    /// app runs under <c>dotnet InterviewFlow.App.dll</c> with no apphost
    /// beside it — a shortcut to dotnet.exe would launch nothing.
    /// </summary>
    public static string? TargetExe()
    {
        var process = Environment.ProcessPath;
        if (process is null)
            return null;
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return process;

        var entry = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entry))
            return null;
        var apphost = Path.ChangeExtension(entry, ".exe");
        return File.Exists(apphost) ? apphost : null;
    }

    /// <summary>The folder whose <c>.lnk</c> files make up that place, per user.</summary>
    public static string Folder(ShortcutPlace place) => place switch
    {
        ShortcutPlace.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ShortcutPlace.StartMenu => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    /// <summary>The link in that place that launches this executable, or null.</summary>
    public static string? Existing(ShortcutPlace place) =>
        TargetExe() is { } target ? Existing(Folder(place), target) : null;

    /// <summary>The first <c>.lnk</c> in <paramref name="folder"/> whose target is <paramref name="target"/>.</summary>
    public static string? Existing(string folder, string target)
    {
        if (!Directory.Exists(folder))
            return null;

        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(folder, "*.lnk");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return links.FirstOrDefault(link => PointsAt(link, target));
    }

    private static bool PointsAt(string link, string target)
    {
        var linkTarget = ShellLink.ReadTargetPath(link);
        if (linkTarget is null)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(linkTarget), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a shortcut to this executable in that place and returns its
    /// path. An unrelated "Interview Flow.lnk" already there (another copy of
    /// the app) is kept; the new one takes a numbered name beside it.
    /// </summary>
    public static string Add(ShortcutPlace place)
    {
        var target = TargetExe()
            ?? throw new InvalidOperationException("The app's executable could not be located.");

        var folder = Folder(place);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ShortcutName + ".lnk");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{ShortcutName} ({n}).lnk");

        var bytes = ShellLink.Create(
            target,
            workingDirectory: Path.GetDirectoryName(target) ?? "",
            description: "Interview Flow — AI interview-prep coach");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Deletes the link in that place that launches this executable, if there is one.</summary>
    public static void Remove(ShortcutPlace place)
    {
        if (Existing(place) is { } link)
            File.Delete(link);
    }
}

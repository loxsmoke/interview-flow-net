using InterviewFlow.App.Platform;

namespace InterviewFlow.Tests.App;

/// <summary>
/// The managed .lnk writer/reader behind the Configuration shortcuts group
/// (docs/03 §3.11). Byte-level, so it runs on every OS; the fixtures are
/// links Windows itself wrote (WScript.Shell), one with an absolute target
/// and one whose target was given as %SystemRoot%, which the shell stores
/// in an EnvironmentVariableDataBlock instead of LinkInfo.
/// </summary>
public sealed class ShellLinkTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Round_trips_the_target_through_link_info()
    {
        var bytes = ShellLink.Create(@"C:\Program Files\Interview Flow\InterviewFlow.App.exe",
            @"C:\Program Files\Interview Flow", "Interview Flow");

        Assert.Equal(@"C:\Program Files\Interview Flow\InterviewFlow.App.exe", ShellLink.ReadTargetPath(bytes));
    }

    [Fact]
    public void Keeps_non_latin_path_segments_in_the_unicode_fields()
    {
        var bytes = ShellLink.Create(@"C:\Users\Žukauskas\Interview Flow\InterviewFlow.App.exe", "");

        Assert.Equal(@"C:\Users\Žukauskas\Interview Flow\InterviewFlow.App.exe", ShellLink.ReadTargetPath(bytes));
    }

    [Fact]
    public void Writes_the_shell_link_header_and_an_item_id_list()
    {
        var bytes = ShellLink.Create(@"C:\Tools\app.exe", @"C:\Tools");

        Assert.Equal(0x4Cu, BitConverter.ToUInt32(bytes, 0));
        var flags = BitConverter.ToUInt32(bytes, 0x14);
        Assert.Equal(0x1u, flags & 0x1);   // HasLinkTargetIDList — Windows resolves from this
        Assert.Equal(0x2u, flags & 0x2);   // HasLinkInfo
        Assert.Equal(0x80u, flags & 0x80); // IsUnicode
        // The list: "My Computer", the drive, the folder, the file, terminator.
        var listSize = BitConverter.ToUInt16(bytes, 0x4C);
        Assert.Equal(0x14, BitConverter.ToUInt16(bytes, 0x4E));
        Assert.Equal(0x19, BitConverter.ToUInt16(bytes, 0x4E + 0x14));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0x4C + listSize));
    }

    [Fact]
    public void Reads_a_link_written_by_windows() =>
        Assert.Equal(@"C:\Windows\System32\cmd.exe", ShellLink.ReadTargetPath(Fixture("windows-shortcut.lnk")));

    [Fact]
    public void Reads_a_target_kept_in_an_environment_variable_block()
    {
        var target = ShellLink.ReadTargetPath(Fixture("windows-shortcut-envvar.lnk"));

        Assert.NotNull(target);
        Assert.EndsWith(@"\System32\cmd.exe", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", target);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Rejects_what_is_not_a_link(byte[] bytes) =>
        Assert.Null(ShellLink.ReadTargetPath(bytes));

    [Fact]
    public void A_text_file_is_not_a_link()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(new string('L', 200));
        Assert.Null(ShellLink.ReadTargetPath(bytes));
    }

    [Fact]
    public void A_missing_file_reads_as_no_target() =>
        Assert.Null(ShellLink.ReadTargetPath(Path.Combine(AppContext.BaseDirectory, "no-such.lnk")));
}

/// <summary>
/// "Same location" for the shortcut buttons: a place holds the app's shortcut
/// only when one of its links targets this executable's path, whatever the
/// link is called — a link to another copy of the app is not it.
/// </summary>
public sealed class WindowsShortcutsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-lnk-" + Guid.NewGuid().ToString("N")[..8]);

    public WindowsShortcutsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private const string Exe = @"C:\Apps\Interview Flow\InterviewFlow.App.exe";

    [Fact]
    public void Finds_the_link_that_targets_this_executable_by_content_not_name()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Interview Flow.lnk"),
            ShellLink.Create(@"C:\Older\InterviewFlow.App.exe", @"C:\Older"));
        File.WriteAllBytes(Path.Combine(_dir, "Prep coach.lnk"),
            ShellLink.Create(Exe, @"C:\Apps\Interview Flow"));
        File.WriteAllText(Path.Combine(_dir, "notes.lnk"), "not a link at all");

        Assert.Equal(Path.Combine(_dir, "Prep coach.lnk"), WindowsShortcuts.Existing(_dir, Exe));
    }

    [Fact]
    public void Compares_paths_case_insensitively()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Interview Flow.lnk"),
            ShellLink.Create(Exe.ToUpperInvariant(), ""));

        Assert.NotNull(WindowsShortcuts.Existing(_dir, Exe));
    }

    [Fact]
    public void Nothing_matching_means_null()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Interview Flow.lnk"),
            ShellLink.Create(@"C:\Older\InterviewFlow.App.exe", ""));

        Assert.Null(WindowsShortcuts.Existing(_dir, Exe));
        Assert.Null(WindowsShortcuts.Existing(Path.Combine(_dir, "missing"), Exe));
    }
}

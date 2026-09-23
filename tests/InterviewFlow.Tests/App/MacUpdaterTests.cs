using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using InterviewFlow.App.Platform;
using InterviewFlow.App.Views;

namespace InterviewFlow.Tests.App;

public sealed class MacUpdaterTests
{
    [Fact]
    public void Bundle_guard_includes_children_but_not_similarly_named_siblings()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "Interview Flow.app");
        Assert.True(MacUpdater.IsInsideBundle(Path.Combine(bundle, "Contents", ".env"), bundle));
        Assert.True(MacUpdater.IsInsideBundle(bundle, bundle));
        Assert.False(MacUpdater.IsInsideBundle(bundle + "-data", bundle));
        Assert.False(MacUpdater.IsInsideBundle(Path.Combine(bundle, "..", "data"), bundle));
    }

    [AvaloniaFact]
    public void Windows_has_no_update_ui_or_native_dependency()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Calling Check must be harmless even though the dylib is absent.
        MacUpdater.CheckForUpdates();
        Assert.False(MacUpdater.IsAvailable);
        var view = new AboutPageView();
        var window = new Window { Content = view };
        window.Show();
        Assert.False(view.FindControl<StackPanel>("MacUpdatePanel")!.IsVisible);
        window.Close();
    }
}

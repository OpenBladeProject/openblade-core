using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WhatsNewShellContractTests
{
    [Fact]
    public void FirstLaunchMakesPendingNotesVisibleAndSerializesThemBeforeConsent()
    {
        string app = Read("App.xaml.cs");
        string window = Read("MainWindow.xaml.cs");
        Assert.Contains("&& !window.NeedsWhatsNew", app, StringComparison.Ordinal);
        int start = window.IndexOf("public async Task StartUpdateCheckingAsync()", StringComparison.Ordinal);
        int end = window.IndexOf("public async ValueTask DisposeAsync()", start, StringComparison.Ordinal);
        string startup = window[start..end];
        Assert.Contains("Interlocked.Exchange(ref updateCheckingStarted, 1)", startup, StringComparison.Ordinal);
        Assert.Contains("if (!localizationSnapshotMode)", startup, StringComparison.Ordinal);
        Assert.Contains("if (NeedsWhatsNew || UpdateViewModel.NeedsConsentPrompt)", startup, StringComparison.Ordinal);
        Assert.True(startup.IndexOf("await rootLoaded.Task.WaitAsync", StringComparison.Ordinal)
            < startup.IndexOf("await ShowWhatsNewIfNeededAsync()", StringComparison.Ordinal));
        Assert.True(startup.IndexOf("await ShowWhatsNewIfNeededAsync()", StringComparison.Ordinal)
            < startup.IndexOf("await ShowUpdateCheckConsentIfNeededAsync()", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyCompletedDialogAcknowledgesReleaseAndEveryPreferenceSaveRetainsIt()
    {
        string dialog = Read("MainWindow.WhatsNew.cs");
        Assert.Contains("if (whatsNewOpen || Volatile.Read(ref disposed) != 0)", dialog, StringComparison.Ordinal);
        Assert.Contains("if (await ShowContentDialogAsync(dialog) is not null && !localizationSnapshotMode)", dialog, StringComparison.Ordinal);
        Assert.Contains("lastSeenWhatsNewVersion = whatsNewRelease.Version;", dialog, StringComparison.Ordinal);
        Assert.Contains("QueuePreferenceSave();", dialog, StringComparison.Ordinal);
        string window = Read("MainWindow.xaml.cs");
        Assert.Contains("lastSeenWhatsNewVersion = preferences.LastSeenWhatsNewVersion;", window, StringComparison.Ordinal);
        Assert.Contains("LastSeenWhatsNewVersion: lastSeenWhatsNewVersion", window, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutCanReopenNotesAndPreviewCapturesTheActualDialog()
    {
        string xaml = Read("MainWindow.xaml");
        Assert.Contains("Click=\"OnShowWhatsNew\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Uid=\"MainWindow_ButtonWhatsNew\"", xaml, StringComparison.Ordinal);
        string harness = Read("App.DeveloperHarness.cs");
        Assert.Contains("\"whats-new\" => \"about\"", harness, StringComparison.Ordinal);
        Assert.Contains("snapshotMainWindow.CreateWhatsNewDialog()", harness, StringComparison.Ordinal);
        Assert.Contains("dialog, Path.Combine(resolvedDirectory, \"main-whats-new.png\")", harness, StringComparison.Ordinal);
    }

    private static string Read(string name, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "..", "..", "src", "OpenBlade.Tray", name)));
}

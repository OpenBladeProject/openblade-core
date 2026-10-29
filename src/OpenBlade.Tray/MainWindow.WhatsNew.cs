using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private readonly TaskCompletionSource rootLoaded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WhatsNewRelease whatsNewRelease;
    private readonly bool suppressWhatsNew;
    private readonly WhatsNewSeenStore whatsNewSeenStore = WhatsNewSeenStore.CreateDefault();
    private string? lastSeenWhatsNewVersion;
    private bool whatsNewOpen;

    public bool NeedsWhatsNew =>
        !suppressWhatsNew && whatsNewRelease.ShouldShow(lastSeenWhatsNewVersion);

    private async Task LoadWhatsNewStateAsync()
    {
        if (suppressWhatsNew)
        {
            return;
        }
        try
        {
            lastSeenWhatsNewVersion = await whatsNewSeenStore.LoadAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            OpenBlade.Contracts.DiagnosticTrace.TraceException(
                "OpenBlade.Tray.MainWindow", "LoadWhatsNewState", exception);
        }
    }

    private async Task ShowWhatsNewIfNeededAsync()
    {
        if (!NeedsWhatsNew)
        {
            return;
        }
        try
        {
            await ShowWhatsNewAsync();
        }
        catch (Exception exception)
        {
            // Release notes are optional; their failure must not prevent update
            // consent or the user's configured automatic checks.
            OpenBlade.Contracts.DiagnosticTrace.TraceException(
                "OpenBlade.Tray.MainWindow", "ShowWhatsNew", exception);
        }
    }

    private async void OnShowWhatsNew(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(ShowWhatsNewAsync, nameof(OnShowWhatsNew));

    private async Task ShowWhatsNewAsync()
    {
        if (whatsNewOpen || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        whatsNewOpen = true;
        try
        {
            ContentDialog dialog = CreateWhatsNewDialog();
            // Only acknowledge a dialog that completed on a live window. Teardown
            // or a missing XamlRoot must leave the next launch eligible.
            if (await ShowContentDialogAsync(dialog) is not null && !localizationSnapshotMode)
            {
                lastSeenWhatsNewVersion = whatsNewRelease.Version;
                await whatsNewSeenStore.SaveAsync(lastSeenWhatsNewVersion);
            }
        }
        finally
        {
            whatsNewOpen = false;
        }
    }

    internal ContentDialog CreateWhatsNewDialog()
    {
        var content = new StackPanel { Spacing = 16, MaxWidth = 480 };
        var version = new TextBlock { Text = whatsNewRelease.Version, TextWrapping = TextWrapping.Wrap };
        AppTypography.Apply(version, AppTypography.Caption);
        content.Children.Add(version);
        foreach (WhatsNewSection section in whatsNewRelease.Sections)
        {
            var group = new StackPanel { Spacing = 8 };
            var heading = new TextBlock
            {
                Text = LocalizeWhatsNewHeading(section.Heading),
                TextWrapping = TextWrapping.Wrap,
            };
            AppTypography.Apply(heading, AppTypography.BodyStrong);
            group.Children.Add(heading);
            foreach (string item in section.Items)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bullet = new TextBlock { Text = "•" };
                AppTypography.Apply(bullet, AppTypography.Body);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                    bullet, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap };
                AppTypography.Apply(text, AppTypography.Body);
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet);
                row.Children.Add(text);
                group.Children.Add(row);
            }
            content.Children.Add(group);
        }
        if (whatsNewRelease.Sections.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = LocalizedResources.Get(
                    "WhatsNew_NoSummary", "See the full changelog for this release."),
                TextWrapping = TextWrapping.Wrap,
            };
            AppTypography.Apply(empty, AppTypography.Body);
            content.Children.Add(empty);
        }
        content.Children.Add(new HyperlinkButton
        {
            Content = LocalizedResources.Get("WhatsNew_FullChangelog", "Full changelog"),
            NavigateUri = new Uri("https://github.com/OSSBlade/openblade-core/blob/main/CHANGELOG.md"),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
        });
        return new ContentDialog
        {
            Title = LocalizedResources.Get("WhatsNew_Title", "What's new"),
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 360,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = LocalizedResources.Get("WhatsNew_Close", "Got it"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = Root.ActualTheme,
        };
    }

    private static string LocalizeWhatsNewHeading(string heading) => heading switch
    {
        "Added" => LocalizedResources.Get("WhatsNew_Added", "Added"),
        "Changed" => LocalizedResources.Get("WhatsNew_Changed", "Changed"),
        "Fixed" => LocalizedResources.Get("WhatsNew_Fixed", "Fixed"),
        "Removed" => LocalizedResources.Get("WhatsNew_Removed", "Removed"),
        "Deprecated" => LocalizedResources.Get("WhatsNew_Deprecated", "Deprecated"),
        "Security" => LocalizedResources.Get("WhatsNew_Security", "Security"),
        _ => heading,
    };
}

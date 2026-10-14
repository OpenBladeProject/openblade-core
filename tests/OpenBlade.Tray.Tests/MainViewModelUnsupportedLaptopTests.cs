using System.IO.Compression;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task UnsupportedLaptop_ReplacesGenericUnavailableStateAndEnablesHelp()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.HelpAddSupportCommand);
        Assert.False(viewModel.IsUnsupportedLaptop);
        Assert.False(command.CanExecute(null));
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            UnsupportedLaptop = true,
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.IsUnsupportedLaptop);
        Assert.False(viewModel.HasHardwareControlUnavailableMessage);
        Assert.Equal(string.Empty, viewModel.HardwareControlUnavailableMessage);
        Assert.Equal("Laptop not supported", viewModel.ProfileStateLabel);
        Assert.Equal("Laptop not supported", viewModel.ControlAccessStatus);
        Assert.Equal("Not supported", viewModel.AboutDetectedLaptopStatus);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task UnsupportedLaptop_OnBatteryShowsNoFanPowerGuidance()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            ActivePowerSource = PowerSource.Battery,
            UnsupportedLaptop = true,
        });
        await WaitUntilAsync(() => viewModel.IsUnsupportedLaptop);

        // The generic unavailable message is hidden for unsupported laptops, so
        // fan guidance must not fall through to advice about AC power.
        Assert.False(viewModel.HasHardwareControlUnavailableMessage);
        Assert.False(viewModel.HasFanControlUnavailableMessage);
        Assert.Equal(string.Empty, viewModel.FanControlUnavailableMessage);
    }

    [Fact]
    public async Task UnverifiedLaptopWithoutUnsupportedFlag_KeepsGenericUnavailableState()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.False(viewModel.IsUnsupportedLaptop);
        Assert.True(viewModel.HasHardwareControlUnavailableMessage);
        Assert.Equal("Laptop not found", viewModel.ProfileStateLabel);
        Assert.Equal("Laptop not found", viewModel.ControlAccessStatus);
        Assert.False(
            Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand).CanExecute(null));
    }

    [Fact]
    public async Task UnsupportedLaptop_IsFalseWhenDisconnectedAndNotifiesOnChange()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        ServiceStatusSnapshot unverified = CreateStatus(verified: false, blocked: true);

        await PushAsync(client, dispatcher, unverified);
        changed.Clear();

        await PushAsync(client, dispatcher, unverified with
        {
            Revision = 2,
            UnsupportedLaptop = true,
        });
        Assert.True(viewModel.IsUnsupportedLaptop);
        Assert.Contains(nameof(MainViewModel.IsUnsupportedLaptop), changed);
        changed.Clear();

        await PushAsync(client, dispatcher, unverified with { Revision = 3 });
        Assert.False(viewModel.IsUnsupportedLaptop);
        Assert.Contains(nameof(MainViewModel.IsUnsupportedLaptop), changed);

        await PushAsync(client, dispatcher, unverified with
        {
            Revision = 4,
            UnsupportedLaptop = true,
        });
        Assert.True(viewModel.IsUnsupportedLaptop);
        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);
        Assert.False(viewModel.IsUnsupportedLaptop);
        Assert.False(
            Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand).CanExecute(null));
    }

    [Fact]
    public async Task HelpAddSupport_SavesIdentityReportThenOpensIssueForm()
    {
        var client = new FakeServiceClient { NextArchive = CreateDiagnosticsArchive() };
        var saver = new FakeDiagnosticArchiveSaver
        {
            NextResult = new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Saved,
                @"C:\Users\Test\Desktop\OpenBlade-identity-report.zip"),
        };
        var launcher = new FakeExternalUriLauncher { SaveCounter = () => saver.ExactCalls };
        await using MainViewModel viewModel = await StartUnsupportedAsync(client, saver, launcher);

        await Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand)
            .ExecuteAsync(null);

        Assert.Equal(1, client.ExportCalls);
        Assert.False(client.LastExportLiveReadingsEnabled);
        Assert.Equal(0, saver.Calls);
        Assert.Equal(1, saver.ExactCalls);
        Assert.Equal("OpenBlade-identity-report.zip", saver.ExactArchive!.SuggestedFileName);
        using (var zip = new ZipArchive(new MemoryStream(saver.ExactArchive.ZipBytes)))
        {
            Assert.Equal(
                ["device-verification.json", "versions.json"],
                zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        }

        Assert.Equal([UnsupportedLaptopReport.IssueFormUri], launcher.Opened);
        Assert.Equal(1, launcher.SavesAtLaunch);
        Assert.Equal(
            @"Identity report saved to C:\Users\Test\Desktop\OpenBlade-identity-report.zip. Attach it to the support form in your browser.",
            viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task HelpAddSupport_WhenSaveCanceled_DoesNotOpenBrowser()
    {
        var client = new FakeServiceClient { NextArchive = CreateDiagnosticsArchive() };
        var saver = new FakeDiagnosticArchiveSaver
        {
            NextResult = new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Canceled,
                null),
        };
        var launcher = new FakeExternalUriLauncher();
        await using MainViewModel viewModel = await StartUnsupportedAsync(client, saver, launcher);

        await Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand)
            .ExecuteAsync(null);

        Assert.Empty(launcher.Opened);
        Assert.Equal("Identity report not saved.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task HelpAddSupport_WhenExportFails_DoesNotSaveOrOpenBrowser()
    {
        var client = new FakeServiceClient { ExportFailure = new IOException("pipe closed") };
        var saver = new FakeDiagnosticArchiveSaver();
        var launcher = new FakeExternalUriLauncher();
        await using MainViewModel viewModel = await StartUnsupportedAsync(client, saver, launcher);

        await Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand)
            .ExecuteAsync(null);

        Assert.Equal(0, saver.ExactCalls);
        Assert.Empty(launcher.Opened);
        Assert.Equal("Identity report failed: pipe closed", viewModel.OperationStatusMessage);
        Assert.Equal(
            "Identity report failed: pipe closed",
            viewModel.TroubleshootingRecentErrorStatus);
    }

    [Fact]
    public async Task HelpAddSupport_WhenBrowserDoesNotOpen_ShowsFallbackLink()
    {
        var client = new FakeServiceClient { NextArchive = CreateDiagnosticsArchive() };
        var saver = new FakeDiagnosticArchiveSaver();
        var launcher = new FakeExternalUriLauncher { Result = false };
        await using MainViewModel viewModel = await StartUnsupportedAsync(client, saver, launcher);

        await Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand)
            .ExecuteAsync(null);

        Assert.StartsWith(
            "Identity report saved to ",
            viewModel.OperationStatusMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            UnsupportedLaptopReport.IssueFormUri.ToString(),
            viewModel.OperationStatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpAddSupport_WhenLauncherThrowsAfterSave_StillReportsSavedPathAndLink()
    {
        var client = new FakeServiceClient { NextArchive = CreateDiagnosticsArchive() };
        var saver = new FakeDiagnosticArchiveSaver();
        var launcher = new FakeExternalUriLauncher
        {
            Failure = new InvalidOperationException("no launcher"),
        };
        await using MainViewModel viewModel = await StartUnsupportedAsync(client, saver, launcher);

        await Assert.IsType<AsyncRelayCommand>(viewModel.HelpAddSupportCommand)
            .ExecuteAsync(null);

        Assert.Equal(1, saver.ExactCalls);
        Assert.Contains("OpenBlade-diagnostics.zip", viewModel.OperationStatusMessage);
        Assert.Contains(
            UnsupportedLaptopReport.IssueFormUri.ToString(),
            viewModel.OperationStatusMessage,
            StringComparison.Ordinal);
    }

    private static async Task<MainViewModel> StartUnsupportedAsync(
        FakeServiceClient client,
        FakeDiagnosticArchiveSaver saver,
        FakeExternalUriLauncher launcher)
    {
        var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            saver,
            new FakeStartupRegistration(false),
            externalUriLauncher: launcher);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            UnsupportedLaptop = true,
        });
        await WaitUntilAsync(() => viewModel.IsUnsupportedLaptop);
        return viewModel;
    }

    private static DiagnosticArchive CreateDiagnosticsArchive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in new[]
            {
                "versions.json",
                "device-verification.json",
                "device.json",
                "logs.jsonl",
                "configuration.json",
            })
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes($"{{\"entry\":\"{name}\"}}"));
            }
        }

        return new DiagnosticArchive("OpenBlade-diagnostics.zip", output.ToArray());
    }
}

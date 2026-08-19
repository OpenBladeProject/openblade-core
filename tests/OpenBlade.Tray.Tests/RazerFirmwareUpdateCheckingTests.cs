using System.Diagnostics;
using System.Net;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerFirmwareUpdateCheckingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("RZ09-0581", "20854")]
    [InlineData("RZ09-0581XYZ", "20854")]
    [InlineData("RZ09-0528", "19821")]
    public void SupportCatalog_UsesExactAdmittedModelFamilies(
        string modelNumber,
        string answerId)
    {
        RazerFirmwareSupportPage page = Assert.IsType<RazerFirmwareSupportPage>(
            RazerFirmwareSupportCatalog.Find(modelNumber));

        Assert.Equal("mysupport.razer.com", page.OfficialPageUri.Host);
        Assert.Contains($"a_id/{answerId}/", page.OfficialPageUri.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RZ09-9999")]
    public void SupportCatalog_RejectsUnsupportedModels(string? modelNumber)
    {
        Assert.Null(RazerFirmwareSupportCatalog.Find(modelNumber));
    }

    [Fact]
    public void PageParser_ReadsUpdaterLabelsWithoutMistakingDatesForVersions()
    {
        const string html = """
            <table>
              <tr><td>BIOS Updater</td><td>v3.01</td><td>July 18, 2026</td></tr>
              <tr><td>EC Firmware Updater</td><td><strong>v1.04</strong></td></tr>
              <tr><td>MCU Firmware Updater</td><td>v1.06</td></tr>
              <tr><td>Thunderbolt 5 Firmware Updater</td><td>v69.1</td></tr>
              <tr><td>Touchpad Firmware Updater</td><td>v1.07</td></tr>
            </table>
            """;

        RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);

        Assert.Equal("3.01", versions.BiosVersion);
        Assert.Equal("1.04", versions.EcVersion);
        Assert.Equal("1.06", versions.McuVersion);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.Listed, versions.BiosStatus);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.Listed, versions.EcStatus);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.Listed, versions.McuStatus);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.NotListed, versions.VbiosStatus);
        Assert.Empty(versions.EffectiveVbiosVersions);
        Assert.Equal("69.1", versions.ThunderboltVersion);
        Assert.Equal("1.07", versions.TouchpadVersion);
    }

    [Fact]
    public void PageParser_ReadsGpuSpecificVbiosVersionsAndIgnoresOlderGenericUpdater()
    {
        const string html = """
            <table>
              <tr><td>vBIOS Firmware Updater</td><td>RTX 5080: v98.03.5C.40.26<br />RTX 5090: v98.03.5C.40.25</td></tr>
              <tr><td>vBIOS Firmware Updater</td><td>v98.05.31.00.63</td></tr>
            </table>
            """;

        RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);

        Assert.Equal(RazerPublishedFirmwareVersionStatus.Listed, versions.VbiosStatus);
        Assert.Equal(
            [
                new RazerPublishedVbiosVersion("98.03.5C.40.26", "RTX 5080"),
                new RazerPublishedVbiosVersion("98.03.5C.40.25", "RTX 5090"),
            ],
            versions.EffectiveVbiosVersions);
    }

    [Fact]
    public void PageParser_RejectsGenericVbiosVersionWithoutAnExactGpuMapping()
    {
        const string html = """
            <table>
              <tr><td>vBIOS Firmware Updater</td><td>v98.05.31.00.63</td></tr>
            </table>
            """;

        RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);

        Assert.Equal(RazerPublishedFirmwareVersionStatus.Unreadable, versions.VbiosStatus);
        Assert.Empty(versions.EffectiveVbiosVersions);
    }

    [Fact]
    public void PageParser_DoesNotBorrowAVersionFromANeighboringUpdater()
    {
        const string html = """
            <table>
              <tr><td>BIOS Firmware Updater</td><td>2.02</td></tr>
              <tr><td>EC Firmware Updater</td><td>v1.09</td></tr>
              <tr><td>MCU Firmware Updater</td><td>v1.09</td></tr>
            </table>
            """;

        RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);

        Assert.Null(versions.BiosVersion);
        Assert.Equal("1.09", versions.EcVersion);
        Assert.Equal("1.09", versions.McuVersion);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Unreadable,
            versions.BiosStatus);
    }

    [Fact]
    public void PageParser_DistinguishesAnAbsentUpdaterFromAnUnreadableRow()
    {
        const string html = """
            <table>
              <tr><td>EC Firmware Updater</td><td>version unavailable</td></tr>
              <tr><td>MCU Firmware Updater</td><td>v1.09</td></tr>
            </table>
            """;

        RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);

        Assert.Equal(RazerPublishedFirmwareVersionStatus.NotListed, versions.BiosStatus);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.Unreadable, versions.EcStatus);
        Assert.Equal(RazerPublishedFirmwareVersionStatus.Listed, versions.McuStatus);
    }

    [Fact]
    public async Task Client_ReadsOnlyTheSuppliedOfficialRazerPage()
    {
        var handler = new StubHandler("""
            <div>BIOS Firmware Updater</div><div>v2.02</div>
            <div>EC Firmware Updater</div><div>v1.09</div>
            <div>MCU Firmware Updater</div><div>v1.09</div>
            <div>Thunderbolt 5 Firmware Updater</div><div>v69.1</div>
            <div>Touchpad Firmware Updater</div><div>v1.07</div>
            """);
        using var httpClient = new HttpClient(handler);
        var client = new RazerFirmwareUpdateClient(httpClient);
        RazerFirmwareSupportPage page = Assert.IsType<RazerFirmwareSupportPage>(
            RazerFirmwareSupportCatalog.Find(RazerBladeModelCatalog.Rz090528ModelNumber));

        RazerPublishedFirmwareVersions versions = await client.GetPublishedVersionsAsync(
            page,
            CancellationToken.None);

        Assert.Equal(page.OfficialPageUri, handler.RequestUri);
        Assert.Equal("2.02", versions.BiosVersion);
        Assert.Equal("1.09", versions.EcVersion);
        Assert.Equal("1.09", versions.McuVersion);
        Assert.Null(versions.ThunderboltVersion);
        Assert.Null(versions.TouchpadVersion);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.NotListed,
            versions.ThunderboltStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.NotListed,
            versions.TouchpadStatus);
    }

    [Fact]
    public async Task Client_RejectsNonRazerMetadataSourceBeforeSending()
    {
        var handler = new StubHandler("unused");
        using var httpClient = new HttpClient(handler);
        var client = new RazerFirmwareUpdateClient(httpClient);
        var page = new RazerFirmwareSupportPage(
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            new Uri("https://example.com/firmware"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetPublishedVersionsAsync(page, CancellationToken.None));
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task Client_RejectsAPageWithoutRecognizedUpdaterVersions()
    {
        using var httpClient = new HttpClient(new StubHandler(
            "<div>No firmware updater versions are published.</div>"));
        var client = new RazerFirmwareUpdateClient(httpClient);
        RazerFirmwareSupportPage page = Assert.IsType<RazerFirmwareSupportPage>(
            RazerFirmwareSupportCatalog.Find(
                RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        RazerFirmwareUpdateException exception = await Assert.ThrowsAsync<
            RazerFirmwareUpdateException>(() => client.GetPublishedVersionsAsync(
                page,
                CancellationToken.None));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task Client_RejectsAnEffectiveUriOutsideTheOfficialRazerHost()
    {
        using var httpClient = new HttpClient(new EffectiveUriHandler(
            new Uri("https://example.com/redirected")));
        var client = new RazerFirmwareUpdateClient(httpClient);
        RazerFirmwareSupportPage page = Assert.IsType<RazerFirmwareSupportPage>(
            RazerFirmwareSupportCatalog.Find(
                RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        RazerFirmwareUpdateException exception = await Assert.ThrowsAsync<
            RazerFirmwareUpdateException>(() => client.GetPublishedVersionsAsync(
                page,
                CancellationToken.None));

        Assert.Contains("unapproved destination", exception.Message);
    }

    [Fact]
    public async Task Client_RejectsAResponseLargerThanTheConfiguredLimit()
    {
        const int maximumResponseBytes = 512 * 1024;
        using var httpClient = new HttpClient(new StubHandler(
            new string('x', maximumResponseBytes + 1)));
        var client = new RazerFirmwareUpdateClient(httpClient);
        RazerFirmwareSupportPage page = Assert.IsType<RazerFirmwareSupportPage>(
            RazerFirmwareSupportCatalog.Find(
                RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        RazerFirmwareUpdateException exception = await Assert.ThrowsAsync<
            RazerFirmwareUpdateException>(() => client.GetPublishedVersionsAsync(
                page,
                CancellationToken.None));

        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task ViewModel_UsesLatestFirmwareTerminologyBeforeChecking()
    {
        var client = new StubClient(new("4.00", "1.04", "1.06"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));

        UpdateModel(viewModel);

        Assert.Equal(
            "Check Razer's official page for the latest firmware versions.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task ViewModel_ShowsInstalledAndPublishedValuesWithComparableMcuFormatting()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(client);
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            "1.6.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        Assert.True(viewModel.IsSupported);
        Assert.Equal("1.06", viewModel.InstalledMcuVersion);
        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal("1.06", viewModel.PublishedMcuVersion);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal("Latest firmware versions loaded from Razer.", viewModel.StatusText);
        Assert.NotNull(viewModel.OfficialSupportUri);
    }

    [Fact]
    public async Task ViewModel_ReportsOnlyNewerPublishedFirmware()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new("3.02", "1.04", "1.07")));
        UpdateModel(viewModel);
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Contains("BIOS 3.02", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.Contains("MCU 1.07", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("EC", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.NotNull(update);
        Assert.Contains(
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            update.Signature,
            StringComparison.Ordinal);
        Assert.False(update.IncludesThunderboltOrTouchpadUpdate);
        Assert.True(update.CanMigrateLegacySignature(update.LegacySignature));
        Assert.Equal(viewModel.OfficialSupportUri, update.OfficialSupportUri);
    }

    [Fact]
    public async Task ViewModel_HidesRz090581FirmwareWhenInstalledReadbackIsUnavailable()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "3.01",
                "1.04",
                "1.06",
                ThunderboltVersion: "69.1",
                ThunderboltStatus: RazerPublishedFirmwareVersionStatus.Listed,
                TouchpadVersion: "1.07",
                TouchpadStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        UpdateModel(viewModel);
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.False(viewModel.ShowsThunderboltFirmware);
        Assert.False(viewModel.ShowsTouchpadFirmware);
        Assert.Equal("Unavailable", viewModel.InstalledThunderboltVersion);
        Assert.Equal("Unavailable", viewModel.InstalledTouchpadVersion);
        Assert.Equal("69.1", viewModel.PublishedThunderboltVersion);
        Assert.Equal("1.07", viewModel.PublishedTouchpadVersion);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Null(update);
    }

    [Fact]
    public async Task ViewModel_ComparesReadableThunderboltAndTouchpadFirmware()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "3.01",
                "1.04",
                "1.06",
                ThunderboltVersion: "69.2",
                ThunderboltStatus: RazerPublishedFirmwareVersionStatus.Listed,
                TouchpadVersion: "1.08",
                TouchpadStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            "1.6.0.0",
            "Unavailable",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            ThunderboltFirmwareVersion: "69.1",
            TouchpadFirmwareVersion: "1.07",
            ThunderboltFirmwareAccess: DeviceFeatureAccess.ReadOnly,
            TouchpadFirmwareAccess: DeviceFeatureAccess.ReadOnly));
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.True(viewModel.ShowsThunderboltFirmware);
        Assert.True(viewModel.ShowsTouchpadFirmware);
        Assert.Contains("Thunderbolt 5 69.2", viewModel.UpdateAvailableSummary);
        Assert.Contains("Touchpad 1.08", viewModel.UpdateAvailableSummary);
        Assert.NotNull(update);
        Assert.Contains("thunderbolt=69.2", update.Signature);
        Assert.Contains("touchpad=1.08", update.Signature);
        Assert.True(update.IncludesThunderboltOrTouchpadUpdate);
        Assert.False(update.CanMigrateLegacySignature(update.LegacySignature));
    }

    [Fact]
    public async Task ViewModel_ShowsOnlyFirmwareWithReadableInstalledValues()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new("3.01", "1.04", "1.06")));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "Unavailable",
            " ",
            "Unavailable",
            "Unavailable",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        Assert.False(viewModel.ShowsBiosFirmware);
        Assert.False(viewModel.ShowsEcFirmware);
        Assert.False(viewModel.ShowsMcuFirmware);
        Assert.False(viewModel.ShowsVbiosFirmware);
        Assert.False(viewModel.ShowsThunderboltFirmware);
        Assert.False(viewModel.ShowsTouchpadFirmware);
        Assert.False(viewModel.ShowsFirmwareVersions);
        Assert.Contains(nameof(viewModel.ShowsFirmwareVersions), changedProperties);

        changedProperties.Clear();
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            "1.6.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            "69.1",
            "1.07"));

        Assert.True(viewModel.ShowsBiosFirmware);
        Assert.True(viewModel.ShowsEcFirmware);
        Assert.True(viewModel.ShowsMcuFirmware);
        Assert.True(viewModel.ShowsVbiosFirmware);
        Assert.True(viewModel.ShowsThunderboltFirmware);
        Assert.True(viewModel.ShowsTouchpadFirmware);
        Assert.True(viewModel.ShowsFirmwareVersions);
        Assert.Contains(nameof(viewModel.ShowsFirmwareVersions), changedProperties);
    }

    [Fact]
    public async Task ViewModel_HidesRz090581PublishedOnlyFirmwareOnRz090528()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "2.02",
                "1.09",
                "1.09",
                ThunderboltVersion: "69.1",
                ThunderboltStatus: RazerPublishedFirmwareVersionStatus.Listed,
                TouchpadVersion: "1.07",
                TouchpadStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "98.03.5C.40.24",
            RazerBladeModelCatalog.Rz090528ModelNumber,
            "NVIDIA GeForce RTX 5090 Laptop GPU"));

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.False(viewModel.ShowsThunderboltFirmware);
        Assert.False(viewModel.ShowsTouchpadFirmware);
        Assert.Equal("Not listed", viewModel.PublishedThunderboltVersion);
        Assert.Equal("Not listed", viewModel.PublishedTouchpadVersion);
        Assert.False(viewModel.IsUpdateAvailable);
    }

    [Fact]
    public async Task ViewModel_ExcludesUnpublishedFirmwareFromUpdateSignature()
    {
        FirmwareUpdateAvailableEventArgs first =
            await GetRz090528UpdateAsync("69.1", "1.07");
        FirmwareUpdateAvailableEventArgs second =
            await GetRz090528UpdateAsync("70.0", "1.08");

        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal(first.LegacySignature, second.LegacySignature);
        Assert.EndsWith(
            "|thunderbolt=-|touchpad=-",
            first.Signature,
            StringComparison.Ordinal);
        Assert.Equal(
            $"{RazerBladeModelCatalog.Rz090528ModelNumber}"
                + "|bios=2.03|ec=1.09|mcu=1.09|vbios=-|vbiosGpu=-",
            first.LegacySignature);
        Assert.Equal(
            "Newer BIOS 2.03 firmware is available from Razer.",
            first.Summary);
    }

    [Fact]
    public async Task ViewModel_SelectsAndComparesOnlyTheInstalledGpuVbios()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "2.02",
                "1.09",
                "1.09",
                VbiosVersions:
                [
                    new("98.03.5C.40.26", "RTX 5080"),
                    new("98.03.5C.40.25", "RTX 5090"),
                ],
                VbiosStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "98.03.5C.40.24",
            RazerBladeModelCatalog.Rz090528ModelNumber,
            "NVIDIA GeForce RTX 5090 Laptop GPU"));
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal("98.03.5C.40.25", viewModel.PublishedVbiosVersion);
        Assert.Contains("VBIOS 98.03.5C.40.25", viewModel.UpdateAvailableSummary);
        Assert.DoesNotContain("98.03.5C.40.26", viewModel.UpdateAvailableSummary);
        Assert.NotNull(update);
        Assert.Contains("vbios=98.03.5C.40.25", update.Signature);
        Assert.Contains("vbiosGpu=RTX 5090", update.Signature);
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("NVIDIA GeForce RTX 5070 Laptop GPU")]
    public async Task ViewModel_FailsClosedWhenPublishedVbiosCannotMatchInstalledGpu(
        string installedGpuName)
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "2.02",
                "1.09",
                "1.09",
                VbiosVersions:
                [
                    new("98.03.5C.40.26", "RTX 5080"),
                    new("98.03.5C.40.25", "RTX 5090"),
                ],
                VbiosStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "98.03.5C.40.24",
            RazerBladeModelCatalog.Rz090528ModelNumber,
            installedGpuName));

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal("Couldn't read", viewModel.PublishedVbiosVersion);
        Assert.False(viewModel.IsUpdateAvailable);
    }

    [Theory]
    [InlineData("1.6.0.0", "1.06")]
    [InlineData("1.6.2.0", "1.6.2.0")]
    [InlineData("1.100.0.0", "1.100.0.0")]
    [InlineData("Unavailable", "Unavailable")]
    [InlineData("1.06", "1.06")]
    public async Task ViewModel_FormatsOnlyPaddedFourPartMcuVersions(
        string installedVersion,
        string expectedPresentation)
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new("3.01", "1.04", "1.06")));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            installedVersion,
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        Assert.Equal(expectedPresentation, viewModel.InstalledMcuVersion);
    }

    [Fact]
    public async Task ViewModel_DoesNotTraceLocalizedUnavailableMcuPresentation()
    {
        using var culture = new CultureScope("qps-ploc");
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        Trace.Listeners.Add(listener);
        try
        {
            await using var viewModel = new FirmwareUpdateAssistantViewModel(
                new StubClient(new("3.01", "1.04", "1.07")));
            viewModel.UpdateInstalledFirmware(new FirmwareVersions(
                "3.01",
                "1.04",
                "Unavailable",
                "95.04.5C.40.A9",
                RazerBladeModelCatalog.OpenBladeTargetModelNumber));

            await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);
            Trace.Flush();

            Assert.DoesNotContain(
                "could not compare MCU firmware versions",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task ViewModel_DistinguishesUnreadableAndUnlistedPublishedVersions()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                BiosVersion: null,
                EcVersion: null,
                McuVersion: "1.06",
                BiosStatus: RazerPublishedFirmwareVersionStatus.Unreadable,
                EcStatus: RazerPublishedFirmwareVersionStatus.NotListed)));
        UpdateModel(viewModel);

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal("Couldn't read", viewModel.PublishedBiosVersion);
        Assert.Equal("Not listed", viewModel.PublishedEcVersion);
        Assert.Equal("1.06", viewModel.PublishedMcuVersion);
        Assert.Equal("Not listed", viewModel.PublishedVbiosVersion);
    }

    [Fact]
    public async Task ViewModel_KeepsOfficialLinkWhenTheCheckFails()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new ThrowingClient());
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "Unavailable",
            RazerBladeModelCatalog.Rz090528ModelNumber));
        Uri? officialPage = viewModel.OfficialSupportUri;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal(officialPage, viewModel.OfficialSupportUri);
        Assert.Contains("official link is still available", viewModel.StatusText);
        Assert.Equal("Not checked", viewModel.PublishedBiosVersion);
    }

    [Fact]
    public async Task ViewModel_HidesTheAssistantForUnsupportedModels()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new("9.9", "9.9", "9.9")));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "1", "1", "1", "1", "RZ09-9999"));

        Assert.False(viewModel.IsSupported);
        Assert.False(viewModel.CheckCommand.CanExecute(null));
        Assert.Null(viewModel.OfficialSupportUri);
    }

    [Fact]
    public async Task ViewModel_DefaultsAutomaticChecksToManualOnly()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = CreateScheduledViewModel(client);

        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(UpdateCheckInterval.ManualOnly, viewModel.Interval);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ViewModel_RunsADueAutomaticCheckAndRecordsSuccess()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = CreateScheduledViewModel(client);
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            Now.AddDays(-2),
            selectedRetryAfter: null);
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
        Assert.Null(viewModel.RetryAfter);
        Assert.Equal(1, preferenceChanges);
    }

    [Fact]
    public async Task ViewModel_RestoresModelScopedPublishedVersionsFromPreferences()
    {
        var client = new StubClient(new("9.9", "9.9", "9.9"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            Now.AddHours(-1),
            selectedRetryAfter: null,
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            selectedCachedBiosVersion: null,
            selectedCachedEcVersion: null,
            selectedCachedMcuVersion: "1.06",
            selectedCachedBiosStatus: RazerPublishedFirmwareVersionStatus.Unreadable,
            selectedCachedEcStatus: RazerPublishedFirmwareVersionStatus.NotListed,
            selectedCachedMcuStatus: RazerPublishedFirmwareVersionStatus.Listed);

        UpdateModel(viewModel);
        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal("Couldn't read", viewModel.PublishedBiosVersion);
        Assert.Equal("Not listed", viewModel.PublishedEcVersion);
        Assert.Equal("1.06", viewModel.PublishedMcuVersion);
        Assert.Equal("Not checked", viewModel.PublishedVbiosVersion);
        Assert.StartsWith("Latest firmware versions last loaded ", viewModel.StatusText);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ViewModel_RestoresCachedUpdateAvailabilityWithoutANetworkCheck()
    {
        var client = new StubClient(new("9.9", "9.9", "9.9"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(
            UpdateCheckInterval.ManualOnly,
            Now.AddHours(-1),
            selectedRetryAfter: null,
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            "3.02",
            "1.04",
            "1.07");
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        UpdateModel(viewModel);
        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Contains("BIOS 3.02", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.Contains("MCU 1.07", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.Equal(viewModel.UpdateAvailableSummary, viewModel.StatusText);
        Assert.NotNull(update);
    }

    [Fact]
    public async Task ViewModel_ReevaluatesCachedAvailabilityWhenInstalledFirmwareChanges()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new("3.02", "1.04", "1.06")));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.03",
            "1.6.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));
        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            "1.6.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Contains("BIOS 3.02", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("EC", viewModel.UpdateAvailableSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_DoesNotLetAnUncachedTimestampSuppressAnAutomaticCheck()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            Now.AddHours(-1),
            selectedRetryAfter: null);

        UpdateModel(viewModel);
        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.Equal("3.01", viewModel.PublishedBiosVersion);
        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
    }

    [Fact]
    public async Task ViewModel_DoesNotLetAnotherModelsCacheSuppressAnAutomaticCheck()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            Now.AddHours(-1),
            selectedRetryAfter: null,
            RazerBladeModelCatalog.Rz090528ModelNumber,
            "2.02",
            "1.09",
            "1.09");

        UpdateModel(viewModel);
        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.Equal(RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            viewModel.CachedModelNumber);
        Assert.Equal("3.01", viewModel.PublishedBiosVersion);
        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
    }

    [Fact]
    public async Task ViewModel_DoesNotLetAnotherModelsRetrySuppressAnAutomaticCheck()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            selectedLastSuccessfulCheckAt: null,
            selectedRetryAfter: Now.AddMinutes(5),
            selectedCachedModelNumber: RazerBladeModelCatalog.Rz090528ModelNumber);

        UpdateModel(viewModel);
        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.Equal(RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            viewModel.CachedModelNumber);
        Assert.Null(viewModel.RetryAfter);
    }

    [Fact]
    public async Task ViewModel_ClearsPublishedVersionsWhenTheModelChanges()
    {
        await using var viewModel = CreateScheduledViewModel(
            new StubClient(new("3.01", "1.04", "1.06")));
        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "Unavailable",
            RazerBladeModelCatalog.Rz090528ModelNumber));

        Assert.Equal("Not checked", viewModel.PublishedBiosVersion);
        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Null(viewModel.CachedModelNumber);
    }

    [Fact]
    public async Task ViewModel_DoesNotRunBeforeTheSelectedIntervalIsDue()
    {
        var client = new StubClient(new("3.01", "1.04", "1.06"));
        await using var viewModel = CreateScheduledViewModel(client);
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Weekly,
            Now.AddDays(-2),
            selectedRetryAfter: null,
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            "3.01",
            "1.04",
            "1.06");

        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ViewModel_FailedCheckRecordsABoundedRetry()
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new ThrowingClient(),
            new FixedTimeProvider(Now));
        UpdateModel(viewModel);
        viewModel.ApplyPreferences(
            UpdateCheckInterval.Daily,
            selectedLastSuccessfulCheckAt: null,
            selectedRetryAfter: null);

        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => !viewModel.IsChecking && viewModel.RetryAfter is not null);

        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(Now.AddMinutes(5), viewModel.RetryAfter);
    }

    [Fact]
    public async Task ViewModel_ChangingTheIntervalRaisesAPreferenceChange()
    {
        await using var viewModel = CreateScheduledViewModel(
            new StubClient(new("3.01", "1.04", "1.06")));
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.SelectedIntervalOption = viewModel.IntervalOptions.Single(option =>
            option.Interval == UpdateCheckInterval.Monthly);

        Assert.Equal(UpdateCheckInterval.Monthly, viewModel.Interval);
        Assert.Equal(1, preferenceChanges);
    }

    private static FirmwareUpdateAssistantViewModel CreateScheduledViewModel(
        IRazerFirmwareUpdateClient client)
    {
        var viewModel = new FirmwareUpdateAssistantViewModel(
            client,
            new FixedTimeProvider(Now));
        UpdateModel(viewModel);
        return viewModel;
    }

    private static async Task<FirmwareUpdateAvailableEventArgs> GetRz090528UpdateAsync(
        string thunderboltVersion,
        string touchpadVersion)
    {
        await using var viewModel = new FirmwareUpdateAssistantViewModel(
            new StubClient(new(
                "2.03",
                "1.09",
                "1.09",
                ThunderboltVersion: thunderboltVersion,
                ThunderboltStatus: RazerPublishedFirmwareVersionStatus.Listed,
                TouchpadVersion: touchpadVersion,
                TouchpadStatus: RazerPublishedFirmwareVersionStatus.Listed)));
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "2.02",
            "1.09",
            "1.9.0.0",
            "Unavailable",
            RazerBladeModelCatalog.Rz090528ModelNumber));
        FirmwareUpdateAvailableEventArgs? update = null;
        viewModel.UpdateAvailable += (_, args) => update = args;

        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        return Assert.IsType<FirmwareUpdateAvailableEventArgs>(update);
    }

    private static void UpdateModel(FirmwareUpdateAssistantViewModel viewModel) =>
        viewModel.UpdateInstalledFirmware(new FirmwareVersions(
            "3.01",
            "1.04",
            "1.6.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber));

    private sealed class StubClient(RazerPublishedFirmwareVersions result)
        : IRazerFirmwareUpdateClient
    {
        public int CallCount { get; private set; }

        public Task<RazerPublishedFirmwareVersions> GetPublishedVersionsAsync(
            RazerFirmwareSupportPage supportPage,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingClient : IRazerFirmwareUpdateClient
    {
        public Task<RazerPublishedFirmwareVersions> GetPublishedVersionsAsync(
            RazerFirmwareSupportPage supportPage,
            CancellationToken cancellationToken) =>
            Task.FromException<RazerPublishedFirmwareVersions>(
                new RazerFirmwareUpdateException("offline"));
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
    }

    private sealed class EffectiveUriHandler(Uri effectiveUri) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, effectiveUri),
                Content = new StringContent(
                    "<div>BIOS Firmware Updater</div><div>v3.01</div>"),
            });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

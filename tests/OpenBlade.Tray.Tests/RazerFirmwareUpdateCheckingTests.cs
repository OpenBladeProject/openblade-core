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
    public async Task ViewModel_ShowsInstalledAndPublishedValuesWithoutComparingThem()
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
        Assert.Equal("1.6.0.0", viewModel.InstalledMcuVersion);
        await ((AsyncRelayCommand)viewModel.CheckCommand).ExecuteAsync(null);

        Assert.Equal("1.06", viewModel.PublishedMcuVersion);
        Assert.Equal("Published updater versions loaded from Razer.", viewModel.StatusText);
        Assert.NotNull(viewModel.OfficialSupportUri);
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
        Assert.Contains("last loaded", viewModel.StatusText);
        Assert.Equal(0, client.CallCount);
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

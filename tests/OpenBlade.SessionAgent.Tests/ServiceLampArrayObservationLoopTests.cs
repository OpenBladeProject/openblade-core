using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceLampArrayObservationLoopTests
{
    [Fact]
    public void EligibilityRequiresAVerifiedReviewedModelInTheActiveSession()
    {
        ServiceStatusSnapshot eligible = Status(access: null);

        LampArraySessionEligibility? result =
            LampArraySessionEligibility.FromStatus(eligible);

        // The advisory needs no LampArray capability: listing the model is
        // the admission, and delegation stays a separate capability.
        Assert.NotNull(result);
        Assert.Equal(RazerBladeModelCatalog.Rz090528ModelNumber, result.Profile.ModelNumber);
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with { DeviceVerified = false }));
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with { ControlAccess = SessionControlAccess.InactiveSession }));
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with { ControlAccess = SessionControlAccess.NotInteractive }));
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with
            {
                Firmware = eligible.Firmware! with
                {
                    ModelNumber = RazerBladeModelCatalog.OpenBladeTargetModelNumber,
                },
            }));
        // Admission is model-scoped: an untested BIOS stays eligible.
        Assert.NotNull(LampArraySessionEligibility.FromStatus(
            eligible with
            {
                Firmware = eligible.Firmware! with { BiosVersion = "2.03" },
            }));
    }

    [Theory]
    [InlineData("RZ09-0528", 0x02C6, false)]
    [InlineData("RZ09-0529", 0x02C7, true)]
    [InlineData("RZ09-0509", 0x02B8, false)]
    [InlineData("RZ09-0510", 0x02B7, false)]
    [InlineData("RZ09-0530", 0x02C5, false)]
    public void EligibilityBindsEachReviewedModelToItsExactLampArrayProductId(
        string modelNumber,
        int productId,
        bool coversVaporChamber)
    {
        LampArraySessionEligibility? result = LampArraySessionEligibility.FromStatus(
            Status(access: null, modelNumber));

        Assert.NotNull(result);
        Assert.Equal(modelNumber, result.Profile.ModelNumber);
        Assert.Equal(productId, result.Profile.ProductId);
        Assert.Equal(coversVaporChamber, result.Profile.CoversVaporChamber);
    }

    [Theory]
    // Neither model is in the reviewed LampArray list.
    [InlineData("RZ09-0483")]
    [InlineData(RazerBladeModelCatalog.OpenBladeTargetModelNumber)]
    public void EligibilityExcludesModelsWithoutAReviewedLampArray(string modelNumber)
    {
        Assert.Null(LampArraySessionEligibility.FromStatus(
            Status(DeviceFeatureAccess.ObservedOnly, modelNumber)));
    }

    [Theory]
    // IsEnabled is the calling app's own update gate, true by default, so it
    // never decides the advisory in either direction.
    [InlineData(nameof(DynamicLightingUserSetting.NotConfigured), true, true, WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData(nameof(DynamicLightingUserSetting.Enabled), true, true, WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData(nameof(DynamicLightingUserSetting.Enabled), true, false, WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData(nameof(DynamicLightingUserSetting.Enabled), true, null, WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData(nameof(DynamicLightingUserSetting.Disabled), true, true, WindowsDynamicLightingAdvisory.Off)]
    [InlineData(nameof(DynamicLightingUserSetting.Enabled), false, true, null)]
    [InlineData(nameof(DynamicLightingUserSetting.Enabled), null, true, null)]
    public void ClassifierReadsOnlyTheGlobalSwitchAndExactArrayPresence(
        string setting,
        bool? connected,
        bool? enabled,
        WindowsDynamicLightingAdvisory? expected)
    {
        LampArrayObservation observation = ApprovedObservation() with
        {
            IsConnected = connected,
            IsEnabled = enabled,
            // The repository's RZ09-0528 capture recorded IsAvailable false.
            IsAvailable = false,
        };

        Assert.Equal(
            expected,
            WindowsDynamicLightingAdvisoryClassifier.Classify(
                observation,
                Enum.Parse<DynamicLightingUserSetting>(setting)));
    }

    [Fact]
    public void ClassifierTreatsAMissingArrayAsOffAndDuplicatesAsUnknown()
    {
        LampArrayObservation absent = ApprovedObservation() with
        {
            ExactDeviceCount = 0,
            IsConnected = null,
            IsEnabled = null,
        };

        Assert.Equal(
            WindowsDynamicLightingAdvisory.Off,
            WindowsDynamicLightingAdvisoryClassifier.Classify(
                absent,
                DynamicLightingUserSetting.Enabled));
        Assert.Null(WindowsDynamicLightingAdvisoryClassifier.Classify(
            ApprovedObservation() with { ExactDeviceCount = 2 },
            DynamicLightingUserSetting.Enabled));
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Off,
            WindowsDynamicLightingAdvisoryClassifier.Classify(
                ApprovedObservation() with { ExactDeviceCount = 2 },
                DynamicLightingUserSetting.Disabled));
    }

    [Theory]
    [InlineData("RZ09-0528", 0x02C6)]
    [InlineData("RZ09-0529", 0x02C7)]
    [InlineData("RZ09-0509", 0x02B8)]
    [InlineData("RZ09-0510", 0x02B7)]
    [InlineData("RZ09-0530", 0x02C5)]
    public async Task ObservationReportsTheAdvisoryForTheExactModelAndProductId(
        string modelNumber,
        int productId)
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var reporter = new Reporter();
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            delay: InfiniteDelayAsync,
            settingSource: new Setting(DynamicLightingUserSetting.NotConfigured),
            advisoryReporter: reporter.ReportAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(access: null, modelNumber));
        await WaitUntilAsync(() => !reporter.Requests.IsEmpty, cancellation.Token);

        ReportWindowsDynamicLightingAdvisoryRequest request = Assert.Single(reporter.Requests);
        Assert.Equal(productId, source.LastProductId);
        Assert.Equal(modelNumber, request.ModelNumber);
        Assert.Equal(WindowsDynamicLightingAdvisory.MayControl, request.Advisory);
        Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task ObservationReportsOnlyWhenTheServiceAdvisoryDiffers()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var reporter = new Reporter();
        using var tick = new SemaphoreSlim(0);
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            delay: (_, token) => tick.WaitAsync(token),
            settingSource: new Setting(DynamicLightingUserSetting.Enabled),
            advisoryReporter: reporter.ReportAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);
        ServiceStatusSnapshot status = Status(access: null, "RZ09-0529");

        watcher.PublishConnected(status);
        await WaitUntilAsync(() => reporter.Requests.Count == 1, cancellation.Token);

        // The service now publishes the reported advisory: no repeat report.
        watcher.PublishConnected(status with
        {
            Revision = 2,
            WindowsDynamicLightingAdvisory = WindowsDynamicLightingAdvisory.MayControl,
        });
        await Task.Delay(30, cancellation.Token);
        tick.Release();
        await WaitUntilAsync(() => source.CallCount == 2, cancellation.Token);
        await Task.Delay(30, cancellation.Token);
        Assert.Single(reporter.Requests);

        // A restarted service or another user publishes Unknown again.
        watcher.PublishConnected(status with { Revision = 3 });
        await Task.Delay(30, cancellation.Token);
        tick.Release();
        await WaitUntilAsync(() => reporter.Requests.Count == 2, cancellation.Token);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task InactiveSessionAgentNeitherObservesNorReports()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var reporter = new Reporter();
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            delay: static (_, _) => Task.CompletedTask,
            settingSource: new Setting(DynamicLightingUserSetting.Enabled),
            advisoryReporter: reporter.ReportAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(access: null, "RZ09-0529") with
        {
            ControlAccess = SessionControlAccess.InactiveSession,
        });
        await Task.Delay(50, cancellation.Token);

        Assert.Equal(0, source.CallCount);
        Assert.Empty(reporter.Requests);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task RefusedReportIsTracedWithoutAFastRetry()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var reporter = new Reporter(ControlOperationStatus.Blocked);
        var failures = new ConcurrentQueue<Exception>();
        var delays = new ConcurrentQueue<TimeSpan>();
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            errorHandler: failures.Enqueue,
            delay: (interval, token) =>
            {
                delays.Enqueue(interval);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            settingSource: new Setting(DynamicLightingUserSetting.Enabled),
            advisoryReporter: reporter.ReportAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(access: null, "RZ09-0529"));
        await WaitUntilAsync(() => !delays.IsEmpty, cancellation.Token);

        Assert.Single(reporter.Requests);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UnreadableWindowsSettingSendsNoReportAndRetries()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var reporter = new Reporter();
        var failures = new ConcurrentQueue<Exception>();
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            errorHandler: failures.Enqueue,
            delay: InfiniteDelayAsync,
            settingSource: new Setting(
                DynamicLightingUserSetting.Enabled,
                new UnauthorizedAccessException()),
            advisoryReporter: reporter.ReportAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(access: null, "RZ09-0529"));
        await WaitUntilAsync(() => !failures.IsEmpty, cancellation.Token);

        Assert.Empty(reporter.Requests);
        Assert.IsType<UnauthorizedAccessException>(Assert.Single(failures));

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public void AdvisoryReportingRequiresBothTheSettingSourceAndReporter()
    {
        var reporter = new Reporter();

        Assert.Throws<ArgumentException>(() => new ServiceLampArrayObservationLoop(
            new Watcher(),
            new Source(ApprovedObservation()),
            advisoryReporter: reporter.ReportAsync));
        Assert.Throws<ArgumentException>(() => new ServiceLampArrayObservationLoop(
            new Watcher(),
            new Source(ApprovedObservation()),
            settingSource: new Setting(DynamicLightingUserSetting.Enabled)));
    }

    [Fact]
    public void ProgramReportsTheAdvisoryThroughItsOwnServiceClient()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");

        Assert.Contains(
            "await using var lampArrayAdvisoryServiceClient = new ServiceClient();",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "settingSource: new WindowsDynamicLightingUserSettingSource()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            ".ReportWindowsDynamicLightingAdvisoryAsync",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedGeometryRequiresTheExactCapturedFingerprint()
    {
        LampArrayObservation approved = ApprovedObservation();

        Assert.True(approved.MatchesApprovedGeometry);
        Assert.False((approved with { ExactDeviceCount = 2 }).MatchesApprovedGeometry);
        Assert.False((approved with { LampCount = 84 }).MatchesApprovedGeometry);
        Assert.False((approved with { Kind = "Chassis" }).MatchesApprovedGeometry);
        Assert.False((approved with { SupportsVirtualKeys = false }).MatchesApprovedGeometry);
        Assert.False((approved with { GeometrySha256 = new string('0', 64) })
            .MatchesApprovedGeometry);
        Assert.Contains("read-only", approved.ToSanitizedSummary("RZ09-0528"), StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationSourceExposesNoMutationSurface()
    {
        string[] methods = typeof(ILampArrayObservationSource)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(["ObserveAsync"], methods);
    }

    [Fact]
    public async Task ExactEligibilityStartsObservationAndUnrelatedStatusDoesNotRestartIt()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var observations = new ConcurrentQueue<LampArrayObservation>();
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            (_, observation) => observations.Enqueue(observation),
            delay: InfiniteDelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(DeviceFeatureAccess.ObservedOnly));
        await WaitUntilAsync(() => source.CallCount == 1, cancellation.Token);
        await WaitUntilAsync(() => observations.Count == 1, cancellation.Token);

        watcher.PublishConnected(
            Status(DeviceFeatureAccess.ObservedOnly) with { Revision = 2 });
        await Task.Delay(30, cancellation.Token);

        Assert.Equal(1, source.CallCount);
        Assert.Single(observations);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task ReconnectAndExactStatusRestartObservation()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            delay: InfiniteDelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(Status(DeviceFeatureAccess.ObservedOnly));
        await WaitUntilAsync(() => source.CallCount == 1, cancellation.Token);

        watcher.PublishReconnecting();
        watcher.PublishConnected(Status(DeviceFeatureAccess.ObservedOnly));
        await WaitUntilAsync(() => source.CallCount == 2, cancellation.Token);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UnverifiedOrUnlistedStatusNeverEnumeratesLampArrays()
    {
        var watcher = new Watcher();
        var source = new Source(ApprovedObservation());
        var loop = new ServiceLampArrayObservationLoop(
            watcher,
            source,
            delay: InfiniteDelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        watcher.PublishConnected(
            Status(DeviceFeatureAccess.ObservedOnly) with { DeviceVerified = false });
        watcher.PublishConnected(
            Status(DeviceFeatureAccess.ObservedOnly, "RZ09-0483"));
        await Task.Delay(30, cancellation.Token);

        Assert.Equal(0, source.CallCount);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public void ProgramSupervisesLampArrayObservationForTheFullProcessLifetime()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");

        Assert.Contains(
            "new ServiceLampArrayObservationLoop(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "lampArrayObservation.RunAsync(lifetime.Token)",
            program,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(program, "lampArrayObservationTask"));
    }

    private static Task InfiniteDelayAsync(
        TimeSpan _,
        CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static LampArrayObservation ApprovedObservation() => new(
        EnumeratedDeviceCount: 1,
        ExactDeviceCount: 1,
        Kind: Rz090528LampArrayGeometry.Kind,
        LampCount: Rz090528LampArrayGeometry.LampCount,
        IsConnected: true,
        IsEnabled: true,
        IsAvailable: false,
        SupportsVirtualKeys: true,
        MinimumUpdateIntervalMicroseconds: 33333,
        GeometrySha256: Rz090528LampArrayGeometry.Sha256);

    private static ServiceStatusSnapshot Status(
        DeviceFeatureAccess? access,
        string modelNumber = RazerBladeModelCatalog.Rz090528ModelNumber)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.Unavailable() with
            {
                LampArrayLightingAccess = access,
            },
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    FanMode.Automatic,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(
                    profiles.BatteryPolicy,
                    protectionEnabled: true,
                    temporaryFullCharge: false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: new FirmwareVersions(
                "2.02",
                "1.09",
                "1.9.0.0",
                "Unavailable",
                modelNumber));
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int start = 0;
        while ((start = value.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += search.Length;
        }
        return count;
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null
            && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.False(string.IsNullOrWhiteSpace(directory));
        return File.ReadAllText(Path.Combine([directory!, .. parts]));
    }

    private sealed class Source(LampArrayObservation observation)
        : ILampArrayObservationSource
    {
        private int callCount;
        private int lastProductId = -1;

        internal int CallCount => Volatile.Read(ref callCount);

        internal int LastProductId => Volatile.Read(ref lastProductId);

        public Task<LampArrayObservation> ObserveAsync(
            ushort productId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref lastProductId, productId);
            Interlocked.Increment(ref callCount);
            return Task.FromResult(observation);
        }
    }

    private sealed class Setting(DynamicLightingUserSetting value, Exception? failure = null)
        : IDynamicLightingUserSettingSource
    {
        public DynamicLightingUserSetting Read() =>
            failure is null ? value : throw failure;
    }

    private sealed class Reporter(ControlOperationStatus result = ControlOperationStatus.Stored)
    {
        internal ConcurrentQueue<ReportWindowsDynamicLightingAdvisoryRequest> Requests { get; } = new();

        internal Task<ControlOperationResult> ReportAsync(
            ReportWindowsDynamicLightingAdvisoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return Task.FromResult(new ControlOperationResult(result, result.ToString()));
        }
    }

    private sealed class Watcher : IServiceStatusWatcher
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();

        internal void PublishConnected(ServiceStatusSnapshot status) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(status));

        internal void PublishReconnecting() =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Reconnecting());

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in
                updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }
    }
}

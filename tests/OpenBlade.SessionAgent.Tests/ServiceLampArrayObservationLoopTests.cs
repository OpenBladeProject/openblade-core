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
    public void EligibilityRequiresVerifiedExactModelAndAdvertisedAccess()
    {
        ServiceStatusSnapshot eligible = Status(DeviceFeatureAccess.ObservedOnly);

        LampArraySessionEligibility? result =
            LampArraySessionEligibility.FromStatus(eligible);

        Assert.NotNull(result);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, result.Access);
        Assert.NotNull(LampArraySessionEligibility.FromStatus(
            eligible with
            {
                Capabilities = eligible.Capabilities! with
                {
                    LampArrayLightingAccess = DeviceFeatureAccess.ReadWrite,
                },
            }));
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with { DeviceVerified = false }));
        Assert.Null(LampArraySessionEligibility.FromStatus(
            eligible with
            {
                Capabilities = eligible.Capabilities! with
                {
                    LampArrayLightingAccess = null,
                },
            }));
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
        Assert.Contains("read-only", approved.ToSanitizedSummary(), StringComparison.Ordinal);
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
            observations.Enqueue,
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
    public async Task UnverifiedOrUnadvertisedStatusNeverEnumeratesLampArrays()
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
        watcher.PublishConnected(Status(access: null));
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

    private static ServiceStatusSnapshot Status(DeviceFeatureAccess? access)
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
                RazerBladeModelCatalog.Rz090528ModelNumber));
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

        internal int CallCount => Volatile.Read(ref callCount);

        public Task<LampArrayObservation> ObserveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref callCount);
            return Task.FromResult(observation);
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

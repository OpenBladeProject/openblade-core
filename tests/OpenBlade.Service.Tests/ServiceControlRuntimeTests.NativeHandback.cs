using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Deployment;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// A crashed native service hands possibly submitted writes to managed. They are
// confirmed by fresh reads, resolved write-ahead, and never replayed.
public sealed partial class ServiceControlRuntimeTests
{
    private const string HandbackEpoch = "22222222222222222222222222222222";
    private const string LaterHandbackEpoch = "33333333333333333333333333333333";
    private const string HandbackSid = "S-1-5-21-1-2-3-1001";
    private const string OtherHandbackSid = "S-1-5-21-1-2-3-1002";
    private const string RecoveryCompleted = "Unexpected service-exit recovery observed fresh exact-device state";
    private readonly List<WindowsNativeEpochFiles> handbackFixtures = [];

    private static readonly DeviceCapabilities HandbackCapabilities = DeviceCapabilities.SafeTargetDefaults() with
    {
        FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve, FanMode.Maximum },
        LightingEffects = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Spectrum },
        FanSpeedRange = new FanSpeedRange(2000, 5400, 100),
    };

    private static NativeHandbackItem HandbackItem(string family, int progress, string? performance = null,
        string? fanMode = null, NativeHandbackBattery? battery = null) =>
        new("aaaaaaaa-bbbb-cccc-dddd-000000000001", family, progress, HandbackSid, performance, fanMode, battery);

    private static WindowsNativeHandback PendingHandback(NativeHandbackItem item, string epoch = HandbackEpoch) =>
        new(epoch, item, null, null, null, null);

    private void DisposeHandbackFixtures()
    {
        foreach (WindowsNativeEpochFiles files in handbackFixtures)
        {
            string root = files.Root;
            files.Dispose();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
        handbackFixtures.Clear();
    }

    // A private fixture standing in for the native epoch namespace. Each record
    // sits in its generation's directory, as Setup writes it.
    private WindowsNativeEpochFiles HandbackFiles(params WindowsNativeHandback[] records)
    {
        WindowsNativeEpochFiles files = WindowsNativeEpochFiles.CreatePrivateFixture();
        handbackFixtures.Add(files);
        foreach (WindowsNativeHandback record in records)
        {
            Directory.CreateDirectory(Path.Combine(files.Root, record.Epoch));
            files.Publish(WindowsNativeHandbackStorage.RecordPath(files, record.Epoch), record.Serialize([]));
        }
        return files;
    }

    // The binding this start's fresh native custody admitted.
    private static NativeHandbackStore StoreFor(WindowsNativeEpochFiles? files, IReadOnlyDictionary<string, string> authenticated) =>
        new(() => new NativeHandbackBinding(authenticated, files is null ? null : files.ReopenLater()));

    // The history commitments this start's fresh native custody computed.
    private static Dictionary<string, string> Bound(params WindowsNativeHandback[] records) =>
        records.ToDictionary(r => r.Epoch, r => r.Commitment, StringComparer.Ordinal);

    private NativeHandbackStore HandbackStore(WindowsNativeHandback handback, out WindowsNativeEpochFiles files)
    {
        files = HandbackFiles(handback);
        return StoreFor(files, Bound(handback));
    }

    // The persisted outcome per item, or null before any resolution.
    private static string? Outcome(WindowsNativeEpochFiles files, string key, string epoch = HandbackEpoch)
    {
        string path = WindowsNativeHandbackStorage.ResolutionPath(files, epoch);
        if (!File.Exists(path)) { return null; }
        string[] lines = File.ReadAllText(path).Split('\n');
        Assert.Equal("OpenBlade.NativeHandbackResolution.v1", lines[0]);
        string value = lines[3 + Array.IndexOf(NativeHandbackStore.Keys, key)];
        return value == "-" ? null : value;
    }

    private async Task<TestContext> StartWithHandbackAsync(NativeHandbackStore store, ConfirmedDeviceState confirmed)
    {
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = confirmed;
        Assert.True(context.Runtime.NativeHandbackRecoveryPending);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        return context;
    }

    // Production composition: the real ServiceStartupCoordinator over the
    // runtime, with a user configuration repository present, so unexpected-exit
    // recovery waits for an authorized active user. A managed run marker is
    // left behind first when recoveringManagedRun is set.
    private async Task<(TestContext Context, FixedUserConfigurationRepository Repository)> StartProductionHandbackAsync(
        NativeHandbackStore? store, ConfirmedDeviceState confirmed, PowerSource powerSource = PowerSource.PluggedIn,
        bool recoveringManagedRun = false, Action<TestContext>? arrange = null)
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: new FixedFanTemperatureSource(new FanTemperatureReadings(65, 60)),
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(HandbackCapabilities),
            userConfigurationRepository: repository,
            initialPowerSource: powerSource,
            nativeHandbackStore: store);
        context.Device.Capabilities = HandbackCapabilities;
        context.Device.ConfirmedState = confirmed;
        context.Device.FanTargetsAvailable = true;
        arrange?.Invoke(context);
        string marker = Path.Combine(directory, "service-running.json");
        if (recoveringManagedRun) { await new ServiceRunMarkerStore(marker).BeginRunAsync(CancellationToken.None); }
        var coordinator = new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(context.Store),
            new PersistentOverrideStore(Path.Combine(directory, "manual-overrides.json")),
            new ServiceRunMarkerStore(marker),
            context.Runtime,
            context.Telemetry,
            NullLogger.Instance);
        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));
        return (context, repository);
    }

    // The active user signs in, then later ticks run every recovery entry point.
    private static async Task ActivateAndTickAsync(TestContext context, string sid = HandbackSid)
    {
        var user = new SecurityIdentifier(sid);
        await context.Runtime.ActivateUserAsync(user, CancellationToken.None);
        for (int tick = 0; tick < 3; tick++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
            await context.Runtime.ActivateUserAsync(user, CancellationToken.None);
            await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
            await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
            await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        }
    }

    private static bool HistoryContains(TestContext context, string text) =>
        context.History.Snapshot().CommandResults.Any(r => r.Detail?.Contains(text, StringComparison.Ordinal) == true
            || r.Action.Contains(text, StringComparison.Ordinal));

    private static bool HistoryHas(TestContext context, string message) =>
        context.History.Snapshot().CommandResults.Any(r => r.Detail == message);

    private static string Describe(TestContext context)
    {
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        return $"FanTargets=[{string.Join(";", context.Device.FanTargets)}] FanWrites=[{string.Join(",", context.Device.FanWrites)}] "
            + $"Setters={context.Device.TotalSetterCalls} External=[{string.Join(",", status.ExternalChanges)}] "
            + $"Unconfirmed=[{string.Join(",", status.UnconfirmedWrites)}] WritesBlocked={status.WritesBlocked} Message={status.Message}";
    }

    private static void AssertNoHardwareWrite(TestContext context) =>
        Assert.True(context.Device.TotalSetterCalls == 0 && context.Device.FanTargets.Count == 0 && context.Device.FanWrites.Count == 0,
            "Managed wrote on native's behalf: " + Describe(context));

    private static RazerProtocolException ThermalRejection(DeviceStateQuery query, ConfirmedDeviceState state) => new(
        "Thermal channels did not report one recognized matching state.", HidProtocolValidationFailure.PayloadValidator)
    {
        StateQuery = query,
        PartialState = new PartialDeviceStateRead(
            state with { Performance = null, Fans = null, HyperBoostActive = null },
            lightingOffConfirmationUnavailable: false,
            [new DeviceStateReadFailure(query, new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance, DeviceSubsystem.Fans },
                "Thermal channels did not report one recognized matching state.")
            {
                ValidationFailure = HidProtocolValidationFailure.PayloadValidator,
            }]),
    };

    [Fact]
    public async Task NativeHandback_MatchingFirmwareResolvesWriteAheadAndIsNotReseeded()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal("matched", Outcome(files, "pending"));
        Assert.True(HistoryHas(context, "Confirmed the Performance change the previous native service was making."));
        // Resolved: a restart, still bound to the same history, neither reseeds
        // nor re-enters recovery, and the record itself is kept.
        Assert.False(StoreFor(files, Bound(record)).Load().RequiresRecovery);
        Assert.True(File.Exists(WindowsNativeHandbackStorage.RecordPath(files, HandbackEpoch)));
    }

    [Fact]
    public async Task NativeHandback_DifferentFirmwareStateIsLatchedAndNeverRewritten()
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("performance", 4, performance: "Performance")), out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Silent, FanMode.Automatic, ConfirmedBattery(80), null));
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal("mismatched", Outcome(files, "pending"));
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.True(HistoryHas(context,
            "The Performance state differs from the change the previous native service was making. OpenBlade left it unchanged; reapply your setting to change it."));
    }

    // Native performance never runs under owned manual fans.
    [Fact]
    public async Task NativeHandback_PerformanceUnderManualFansIsAMismatch()
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("performance", 2, performance: "Balanced")), out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Fixed, ConfirmedBattery(80), null));

        Assert.Equal("mismatched", Outcome(files, "pending"));
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    // A native HyperBoost exit stopped before its first setter leaves a pending
    // Performance item while firmware still reports HyperBoost, which reads with
    // no preset. That is a definite other state, so the item is latched for a
    // reapply instead of waiting for a preset that never comes; a reading with no
    // preset and no HyperBoost still waits. Nothing is written either way.
    [Theory]
    [InlineData(true, "mismatched")]
    [InlineData(null, null)]
    public async Task NativeHandback_PendingPerformanceUnderHyperBoostIsAMismatch(bool? hyperBoost, string? outcome)
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(null, FanMode.Automatic, ConfirmedBattery(80), null) { HyperBoostActive = hyperBoost });
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);

        Assert.Equal(outcome, Outcome(files, "pending"));
        Assert.Equal(outcome is null, context.Runtime.GetStatus().UnconfirmedWrites.Contains(DeviceSubsystem.Performance));
        Assert.Equal(outcome is null, StoreFor(files, Bound(record)).Load().RequiresRecovery);
        AssertNoHardwareWrite(context);
    }

    // Native owned HyperBoost's enable pair is confirmed as managed confirms its
    // own entry: HyperBoost with Automatic fans. HyperBoost that is off, or that
    // reads with manual fans, is a definite other state; unknown HyperBoost
    // activity keeps the item pending. Nothing is written either way.
    [Theory]
    [InlineData(1, true, FanMode.Automatic, "matched")]
    [InlineData(4, true, FanMode.Automatic, "matched")]
    [InlineData(2, false, FanMode.Automatic, "mismatched")]
    [InlineData(3, true, FanMode.Fixed, "mismatched")]
    [InlineData(2, null, FanMode.Automatic, null)]
    public async Task NativeHandback_PendingHyperBoostIsConfirmedAsManagedEntry(int progress, bool? active, FanMode fans, string? outcome)
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("hyperBoost", progress)), out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, fans, ConfirmedBattery(80), null) { HyperBoostActive = active });
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);

        Assert.Equal(outcome, Outcome(files, "pending"));
        Assert.Equal(outcome is null, context.Runtime.GetStatus().UnconfirmedWrites.Contains(DeviceSubsystem.Performance));
        Assert.Equal(outcome == "mismatched", context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Performance));
        if (outcome == "matched") { Assert.True(HistoryHas(context, "Confirmed the Performance change the previous native service was making.")); }
        Assert.Equal(outcome is null, StoreFor(files, Bound(PendingHandback(HandbackItem("hyperBoost", progress)))).Load().RequiresRecovery);
        AssertNoHardwareWrite(context);
    }

    // Native fan targets compare by Automatic versus manual topology.
    [Theory]
    [InlineData(12, "Automatic", FanMode.Automatic, "matched")]
    [InlineData(12, "Automatic", FanMode.Fixed, "mismatched")]
    [InlineData(42, "Automatic", FanMode.Maximum, "mismatched")]
    [InlineData(14, "Fixed", FanMode.Maximum, "matched")]
    [InlineData(14, "Fixed", FanMode.Automatic, "mismatched")]
    public async Task NativeHandback_PendingFanIsConfirmedByTopology(int progress, string target, FanMode firmware, string outcome)
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("fan", progress, fanMode: target)), out WindowsNativeEpochFiles files);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, null, ConfirmedBattery(80), null);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        // Seeded: an unreadable fan mode keeps the fan item pending.
        Assert.Contains(DeviceSubsystem.Fans, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Null(Outcome(files, "pending"));

        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, firmware, ConfirmedBattery(80), null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(outcome, Outcome(files, "pending"));
        Assert.DoesNotContain(DeviceSubsystem.Fans, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(outcome == "mismatched", context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Fans));
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task NativeHandback_UnreadableFirmwareKeepsThermalWritesBlocked()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(null, FanMode.Automatic, ConfirmedBattery(80), null));

        ControlOperationResult reapply = await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, reapply.Status);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Null(Outcome(files, "pending"));
        Assert.True(StoreFor(files, Bound(record)).Load().RequiresRecovery);
    }

    // Native sets the two thermal channels in turn. A crash between them leaves
    // a pair that never reads as one state; a second fresh observation of the
    // same disagreement resolves it as a mismatch. Any other thermal read
    // failure keeps waiting.
    [Theory]
    [InlineData("performance", true)]
    [InlineData("fan", true)]
    [InlineData("performance", false)]
    [InlineData("fan", false)]
    [InlineData("hyperBoost", true)]
    [InlineData("hyperBoost", false)]
    public async Task NativeHandback_DisagreeingThermalChannelsResolveAsAMismatch(string family, bool disagreement)
    {
        NativeHandbackItem item = family switch
        {
            "performance" => HandbackItem("performance", 2, performance: "Balanced"),
            "hyperBoost" => HandbackItem("hyperBoost", 2),
            _ => HandbackItem("fan", 12, fanMode: "Automatic"),
        };
        WindowsNativeHandback record = PendingHandback(item);
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = state;
        context.Device.ReadStateFailure = ThermalRejection(
            disagreement ? DeviceStateQuery.ThermalChannelAgreement : DeviceStateQuery.ThermalChannelOne, state);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        DeviceSubsystem subsystem = family == "fan" ? DeviceSubsystem.Fans : DeviceSubsystem.Performance;
        Assert.Equal(disagreement ? "mismatched" : null, Outcome(files, "pending"));
        Assert.Equal(!disagreement, context.Runtime.GetStatus().UnconfirmedWrites.Contains(subsystem));
        Assert.Equal(disagreement, context.Runtime.GetStatus().ExternalChanges.Contains(subsystem));
        Assert.Equal(!disagreement, StoreFor(files, Bound(record)).Load().RequiresRecovery);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task NativeHandback_UnpersistedResolutionStaysPendingUntilItCanBeWritten()
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("performance", 2, performance: "Balanced")), out WindowsNativeEpochFiles files);
        string resolution = WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        Assert.True(context.Runtime.NativeHandbackRecoveryPending);
        // Nothing can be published at the resolution path.
        Directory.CreateDirectory(resolution);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None)).Status);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);

        Directory.Delete(resolution);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal("matched", Outcome(files, "pending"));
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task NativeHandback_UnreadableRecordBlocksWithoutGuessing()
    {
        WindowsNativeEpochFiles files = HandbackFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, HandbackEpoch));
        files.Publish(WindowsNativeHandbackStorage.RecordPath(files, HandbackEpoch), "{"u8.ToArray());
        TestContext context = await StartWithHandbackAsync(StoreFor(files, new Dictionary<string, string> { [HandbackEpoch] = new string('a', 64) }),
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));

        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None)).Status);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.False(File.Exists(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch)));
        Assert.True(HistoryContains(context, "could not be read"));
        Assert.Equal(new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans, DeviceSubsystem.Battery }.Order(),
            context.Runtime.GetStatus().UnconfirmedWrites.Order());
    }

    // The record parser refuses a slot Classify never produces and a battery
    // state managed cannot hold; either blocks instead of breaking Initialize.
    [Theory]
    [InlineData("impossible-battery")]
    [InlineData("performance-as-live-charge")]
    public async Task NativeHandback_IncoherentRecordBlocksWithoutBreakingInitialize(string kind)
    {
        WindowsNativeHandback record = kind == "impossible-battery"
            ? PendingHandback(HandbackItem("temporaryCharge", 70, battery: new(80, false, true)))
            : new(HandbackEpoch, null, null, HandbackItem("performance", 2, performance: "Balanced"), null, null);
        WindowsNativeEpochFiles files = HandbackFiles(record);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: StoreFor(files, Bound(record)));
        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        Assert.True(context.Runtime.NativeHandbackRecoveryPending);
        BatteryConfiguration configuration = await context.Store.LoadAsync(CancellationToken.None);

        Exception? error = Record.Exception(() => context.Runtime.Initialize(configuration, PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false));
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Null(error);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.True(HistoryContains(context, "could not be read"));
        Assert.False(File.Exists(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch)));
    }

    [Fact]
    public async Task NativeHandback_InheritedManualFanIsReportedAndLeftToManualTopology()
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: "Fixed"), null, null, null);
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Fixed, ConfirmedBattery(80), null));

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal("reported", Outcome(files, "ownedFan"));
        Assert.True(HistoryHas(context,
            "The previous native service stopped while it controlled the fans. OpenBlade left them unchanged; reapply your fan setting to take control."));
        Assert.False(StoreFor(files, Bound(record)).Load().RequiresRecovery);
    }

    // Prepared (0) and release preparation (31) precede any setter.
    [Theory]
    [InlineData("performance", 0)]
    [InlineData("hyperBoost", 0)]
    [InlineData("fan", 31)]
    public async Task NativeHandback_UnsentPreparationIsReportedOnly(string family, int progress)
    {
        NativeHandbackItem item = family switch
        {
            "performance" => HandbackItem("performance", progress, performance: "Performance"),
            "hyperBoost" => HandbackItem("hyperBoost", progress),
            _ => HandbackItem("fan", progress, fanMode: "Automatic"),
        };
        WindowsNativeHandback record = new(HandbackEpoch, null, null, null, item, null);
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Fixed, ConfirmedBattery(80), null));

        Assert.Equal("reported", Outcome(files, "abandonedPrepared"));
        Assert.True(HistoryHas(context,
            "The previous native service stopped before sending a prepared change to the laptop. Nothing was sent, and OpenBlade did not resend it."));
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task NativeHandback_PowerSourceChangeDoesNotDischargeTheObligation()
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("performance", 2, performance: "Balanced")), out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(null, FanMode.Automatic, ConfirmedBattery(80), null));
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Null(Outcome(files, "pending"));
        Assert.Equal(0, context.Device.PerformanceSetCalls);

        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal("matched", Outcome(files, "pending"));
    }

    // Deferred unexpected-exit recovery completes only once nothing is pending.
    // A handback target comes from its record, so it resolves before the user
    // signs in, and recovery then completes at activation. No family writes on
    // native's behalf, at start, on later ticks or at shutdown.
    [Theory]
    [InlineData("none", null, null)]
    [InlineData("performance", "pending", "matched")]
    [InlineData("fan", "pending", "matched")]
    [InlineData("fan-release", "pending", "matched")]
    [InlineData("battery", "pending", "matched")]
    [InlineData("temporary-charge", "pending", "continuing")]
    [InlineData("live-temporary-charge", "liveTemporaryCharge", "continuing")]
    [InlineData("owned-fan", "ownedFan", "reported")]
    [InlineData("prepared", "abandonedPrepared", "reported")]
    public async Task NativeHandback_ProductionRecoveryResolvesBeforeUserAndCompletesAtActivation(string family, string? key, string? outcome)
    {
        bool temporary = family is "temporary-charge" or "live-temporary-charge";
        WindowsNativeHandback? record = family switch
        {
            "performance" => PendingHandback(HandbackItem("performance", 2, performance: "Balanced")),
            "fan" => PendingHandback(HandbackItem("fan", 12, fanMode: "Automatic")),
            "fan-release" => PendingHandback(HandbackItem("fan", 41, fanMode: "Automatic")),
            "battery" => PendingHandback(HandbackItem("battery", 60, battery: new(80, true, false))),
            "temporary-charge" => PendingHandback(HandbackItem("temporaryCharge", 70, battery: new(80, true, true))),
            "live-temporary-charge" => new(HandbackEpoch, null, null, HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null),
            "owned-fan" => new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: "Fixed"), null, null, null),
            "prepared" => new(HandbackEpoch, null, null, null, HandbackItem("performance", 0, performance: "Performance"), null),
            _ => null,
        };
        WindowsNativeEpochFiles? files = record is null ? null : HandbackFiles(record);
        // Without a record, only a managed run marker forces this recovery.
        var (context, repository) = await StartProductionHandbackAsync(
            record is null ? null : StoreFor(files, Bound(record)),
            new ConfirmedDeviceState(PerformancePreset.Balanced, family == "owned-fan" ? FanMode.Fixed : FanMode.Automatic,
                new ConfirmedBatteryState(true, 80, temporary), LightingSettingsFactory.Off()),
            recoveringManagedRun: record is null);

        // Resolved from fresh reads before any user configuration is loaded.
        Assert.Equal(0, repository.LoadCount);
        if (files is not null) { Assert.Equal(outcome, Outcome(files, key!)); }
        Assert.True(context.Runtime.GetStatus().UnconfirmedWrites.Count == 0, Describe(context));
        AssertNoHardwareWrite(context);

        await ActivateAndTickAsync(context);

        Assert.Equal(1, repository.LoadCount);
        Assert.True(HistoryContains(context, RecoveryCompleted), Describe(context));
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.False(context.Runtime.GetStatus().WritesBlocked, Describe(context));
        AssertNoHardwareWrite(context);
        if (files is not null) { Assert.False(StoreFor(files, Bound(record!)).Load().RequiresRecovery); }

        await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);
        AssertNoHardwareWrite(context);
    }

    // Only a managed run marker is evidence that managed owned a manual fan.
    // Without it, an inherited native manual fan is reported for an explicit
    // reapply and never adopted; with it, the existing safety floor applies.
    [Theory]
    [InlineData(PowerSource.PluggedIn, FanMode.Fixed, "Fixed")]
    [InlineData(PowerSource.Battery, FanMode.Fixed, "Fixed")]
    [InlineData(PowerSource.PluggedIn, FanMode.Maximum, "Maximum")]
    public async Task NativeHandback_InheritedManualFanIsNeverWrittenWithoutManagedRunEvidence(PowerSource powerSource, FanMode firmware, string native)
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: native), null, null, null);
        var (context, _) = await StartProductionHandbackAsync(StoreFor(HandbackFiles(record), Bound(record)),
            new ConfirmedDeviceState(PerformancePreset.Balanced, firmware, ConfirmedBattery(80), LightingSettingsFactory.Off()), powerSource);
        AssertNoHardwareWrite(context);

        await ActivateAndTickAsync(context);
        AssertNoHardwareWrite(context);
        Assert.True(HistoryContains(context, RecoveryCompleted), Describe(context));
        Assert.Contains(DeviceSubsystem.Fans, context.Runtime.GetStatus().ExternalChanges);

        await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);
        AssertNoHardwareWrite(context);
    }

    [Fact]
    public async Task NativeHandback_ManagedRunMarkerKeepsItsFanSafetyFloor()
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: "Fixed"), null, null, null);
        var (context, _) = await StartProductionHandbackAsync(StoreFor(HandbackFiles(record), Bound(record)),
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Fixed, ConfirmedBattery(80), LightingSettingsFactory.Off()),
            recoveringManagedRun: true);

        Assert.NotEmpty(context.Device.FanTargets);
    }

    // Outside the deferred composition, curve recovery is not armed either.
    [Fact]
    public async Task NativeHandback_RuntimeInitializeWithoutManagedRunArmsNoFanRecovery()
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: "Fixed"), null, null, null);
        TestContext context = CreateContext(providerAvailable: true, initialize: false,
            nativeHandbackStore: StoreFor(HandbackFiles(record), Bound(record)));
        context.Device.Capabilities = HandbackCapabilities;
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.ManualCurve, ConfirmedBattery(80), null);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);

        AssertNoHardwareWrite(context);
    }

    // A confirmed native temporary full charge continues only for the user who
    // started it, and only when that user's battery settings include it.
    [Theory]
    [InlineData(HandbackSid, PowerSource.PluggedIn, "Temporary full charge started by the previous native service continues for the signed-in user.", true)]
    [InlineData(OtherHandbackSid, PowerSource.PluggedIn, "Temporary full charge started by the previous native service is still active for another user.", false)]
    [InlineData(HandbackSid, PowerSource.Battery, "Temporary full charge started by the previous native service is still active, but your battery settings do not include it. Reapply your battery setting to change it.", false)]
    public async Task NativeHandback_LiveTemporaryChargeFollowsTheRequestingUserOnly(string activeSid, PowerSource powerSource, string message, bool inherits)
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, null, HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null);
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var (context, _) = await StartProductionHandbackAsync(StoreFor(files, Bound(record)),
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, true), LightingSettingsFactory.Off()),
            powerSource);

        Assert.Equal(NativeHandbackStore.Continuing, Outcome(files, "liveTemporaryCharge"));
        Assert.True(HistoryHas(context,
            "Temporary full charge started by the previous native service is still active. OpenBlade continues it for the user who started it when they sign in."));
        Assert.False(context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);

        await ActivateAndTickAsync(context, activeSid);

        Assert.True(HistoryHas(context, message), Describe(context));
        Assert.Equal(inherits ? "matched" : "reported", Outcome(files, "liveTemporaryCharge"));
        Assert.Equal(inherits, context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);
        Assert.Equal(0, context.Device.BatterySetCalls);
        AssertNoHardwareWrite(context);
    }

    // A live charge that ended naturally is not a mismatch to reapply.
    [Fact]
    public async Task NativeHandback_EndedTemporaryChargeIsReportedWithoutALatch()
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, null, HandbackItem("temporaryCharge", 73, battery: new(80, true, true)), null, null);
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, false), null));

        Assert.Equal("reported", Outcome(files, "liveTemporaryCharge"));
        Assert.True(HistoryHas(context, "Temporary full charge started by the previous native service has ended."));
        Assert.DoesNotContain(DeviceSubsystem.Battery, context.Runtime.GetStatus().ExternalChanges);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    // A temporary charge native was still sending is confirmed strictly.
    [Fact]
    public async Task NativeHandback_UnconfirmedTemporaryChargeRequestStaysAMismatch()
    {
        NativeHandbackStore store = HandbackStore(PendingHandback(HandbackItem("temporaryCharge", 70, battery: new(80, true, true))), out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, false), null));

        Assert.Equal("mismatched", Outcome(files, "pending"));
        Assert.Contains(DeviceSubsystem.Battery, context.Runtime.GetStatus().ExternalChanges);
    }

    // The Service adopts a record only when this start's fresh native custody
    // computed the same history commitment for that generation. Custody itself
    // refuses a record beside a generation it does not hand back.
    [Theory]
    [InlineData("other-history")]
    [InlineData("missing-record")]
    [InlineData("missing-view")]
    [InlineData("two-generations")]
    [InlineData("foreign-resolution")]
    [InlineData("resolution-for-absent-item")]
    [InlineData("continuing-without-a-charge")]
    public void NativeHandback_StoreRefusesUnboundOrUnknownEvidence(string fault)
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeHandback later = PendingHandback(HandbackItem("battery", 60, battery: new(80, true, false)), LaterHandbackEpoch);
        WindowsNativeEpochFiles files = fault == "two-generations" ? HandbackFiles(record, later) : HandbackFiles(record);
        IReadOnlyDictionary<string, string> bound = fault switch
        {
            "other-history" => new Dictionary<string, string> { [HandbackEpoch] = later.Commitment },
            "missing-record" or "two-generations" => Bound(record, later),
            _ => Bound(record),
        };
        string? resolution = fault switch
        {
            "foreign-resolution" => new string('b', 64) + ",matched,-,-,-",
            // The record holds only a pending item; an outcome for its owned fan is unknown.
            "resolution-for-absent-item" => record.RecordCommitment([]) + ",-,reported,-,-",
            // Only a temporary full charge can wait for its owner's sign-in.
            "continuing-without-a-charge" => record.RecordCommitment([]) + ",continuing,-,-,-",
            _ => null,
        };
        if (resolution is not null)
        {
            files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch), NativeCanonical.Lines(
                "OpenBlade.NativeHandbackResolution.v1", [HandbackEpoch, .. resolution.Split(',')]));
        }

        NativeHandbackAdoption adoption = StoreFor(fault == "missing-view" ? null : files, bound).Load();

        Assert.NotNull(adoption.Failure);
        Assert.Empty(adoption.Obligations);
        Assert.True(adoption.RequiresRecovery);
    }

    // A custody refusal, or an admission that never opened, is not "no
    // handback": it propagates instead of becoming a handback failure.
    [Fact]
    public void NativeHandback_StoreLetsACustodyFailurePropagate()
    {
        IOException refused = new("Native startup custody is unresolved; managed operation remains fenced.");
        Assert.Same(refused, Assert.Throws<IOException>(() => new NativeHandbackStore(() => throw refused).Load()));
        Assert.Throws<InvalidOperationException>(() => new NativeHandbackStore(
            () => throw new InvalidOperationException("Native custody admission is not open.")).Load());
        Assert.Same(NativeHandbackAdoption.None, StoreFor(null, new Dictionary<string, string>()).Load());
    }

    [Fact]
    public void NativeHandback_StoreAdmitsABoundResolvedRecord()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("battery", 60, battery: new(80, true, false)));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        NativeHandbackStore store = StoreFor(files, Bound(record));
        NativeHandbackObligation obligation = Assert.Single(store.Load().Obligations);
        Assert.True(store.TryResolve(new(obligation.Epoch, obligation.RecordCommitment, [obligation.Key], null, false), "matched"));

        NativeHandbackAdoption adoption = StoreFor(files, Bound(record)).Load();

        Assert.Null(adoption.Failure);
        Assert.Empty(adoption.Obligations);
        Assert.False(adoption.RequiresRecovery);
    }

    // A resolution is written only for the exact record it was read from.
    [Fact]
    public void NativeHandback_StoreResolvesOnlyTheExactRecord()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        NativeHandbackObligation obligation = Assert.Single(store.Load().Obligations);

        Assert.False(store.TryResolve(new(HandbackEpoch, new string('b', 64), ["pending"], null, false), "matched"));
        Assert.False(store.TryResolve(new(HandbackEpoch, obligation.RecordCommitment, ["ownedFan"], null, false), "matched"));
        Assert.False(store.TryResolve(NativeHandbackClaim.Unresolvable, "matched"));
        Assert.Null(Outcome(files, "pending"));

        Assert.True(store.TryResolve(new(HandbackEpoch, obligation.RecordCommitment, ["pending"], null, false), "mismatched"));
        // An existing outcome is never replaced.
        Assert.True(store.TryResolve(new(HandbackEpoch, obligation.RecordCommitment, ["pending"], null, false), "matched"));
        Assert.Equal("mismatched", Outcome(files, "pending"));
        Assert.Null(StoreFor(files, Bound(record)).Load().Failure);
    }
    // The production path from startup admission: ServiceMailboxHandoff carries
    // custody's binding, ServiceComposition builds the store over it, and the
    // real ServiceStartupCoordinator resolves the record before any user.
    [Fact]
    public async Task NativeHandback_ProductionCompositionReadsTheAdmittedBinding()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var handoff = new ServiceMailboxHandoff(new ManagedMailboxStartup.Admission(null, null, null,
            new NativeHandbackBinding(Bound(record), files.ReopenLater())));
        using ServiceProvider provider = new ServiceCollection().AddSingleton(handoff).BuildServiceProvider();
        NativeHandbackStore store = ServiceComposition.CreateNativeHandbackStore(provider);

        var (context, repository) = await StartProductionHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), LightingSettingsFactory.Off()));

        Assert.Equal("matched", Outcome(files, "pending"));
        Assert.Equal(0, repository.LoadCount);
        await ActivateAndTickAsync(context);
        Assert.True(HistoryContains(context, RecoveryCompleted), Describe(context));
        AssertNoHardwareWrite(context);
        // Without an admission there is no evidence of "no handback".
        Assert.Throws<InvalidOperationException>(() => new ServiceMailboxHandoff(new NoMailboxRun()).NativeHandbacks);
    }

    // Production order: the handoff constructor does no I/O, and the hosted
    // ServiceMailboxCustodyStartup opens custody before Worker. The store is a
    // lazy delegate: composing the runtime never evaluates it, and its first
    // evaluation is InitializeAsync from Worker.ExecuteAsync, after every
    // hosted-service constructor. Before Open the handoff fails closed, so the
    // coordinator refuses to start instead of reading "no handback".
    [Fact]
    public async Task NativeHandback_StoreIsNeverEvaluatedBeforeCustodyOpens()
    {
        var handoff = new ServiceMailboxHandoff();
        using ServiceProvider provider = new ServiceCollection().AddSingleton(handoff).BuildServiceProvider();
        int reads = 0;
        NativeHandbackStore production = ServiceComposition.CreateNativeHandbackStore(provider);
        var counting = new NativeHandbackStore(() => { reads++; return handoff.NativeHandbacks; });

        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: production);
        TestContext counted = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: counting);
        Assert.Equal(0, reads);

        Assert.Throws<InvalidOperationException>(() => production.Load());
        Assert.Throws<InvalidOperationException>(() => counted.Runtime.NativeHandbackRecoveryPending);
        Assert.Equal(1, reads);
        var coordinator = new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(context.Store),
            new PersistentOverrideStore(Path.Combine(directory, "manual-overrides.json")),
            new ServiceRunMarkerStore(Path.Combine(directory, "service-running.json")),
            context.Runtime,
            context.Telemetry,
            NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync(CancellationToken.None));
        Assert.True(context.Device.TotalSetterCalls == 0 && context.Device.FanTargets.Count == 0 && context.Device.FanWrites.Count == 0);
    }

    private sealed class NoMailboxRun : IManagedMailboxRun
    {
        public void PublishQuiescent() { }
        public void Dispose() { }
    }

    // Uninstall-and-purge deletes OpenBlade's machine data but never the native
    // namespace that holds the record beside the history binding it. A start
    // after purge and reinstall finds the resolved record and starts normally.
    [Fact]
    public async Task NativeHandback_StartAfterUninstallAndPurgeKeepsTheResolvedRecord()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        await StartWithHandbackAsync(StoreFor(files, Bound(record)),
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));
        Assert.Equal("matched", Outcome(files, "pending"));
        Assert.Equal(Path.Combine(files.Root, HandbackEpoch), Path.GetDirectoryName(WindowsNativeHandbackStorage.RecordPath(files, HandbackEpoch)));

        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry)) { Directory.Delete(entry, recursive: true); } else { File.Delete(entry); }
        }
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: StoreFor(files, Bound(record)));
        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);

        Assert.False(context.Runtime.NativeHandbackRecoveryPending);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn, PersistentOverrideState.Empty);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.False(context.Runtime.GetStatus().WritesBlocked, Describe(context));
    }

    // A confirmed live charge stays continuing until its owner signs in. After
    // a Service restart before that sign-in, a fresh read confirms it again,
    // because it may have ended in between: an active charge still continues
    // for its owner, and an ended one is reported. Neither writes the battery.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHandback_TemporaryChargeIsConfirmedAgainAfterAServiceRestartBeforeSignIn(bool stillActive)
    {
        WindowsNativeHandback record = new(HandbackEpoch, null, null, HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null);
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var confirmed = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, true), LightingSettingsFactory.Off());
        await StartProductionHandbackAsync(StoreFor(files, Bound(record)), confirmed);
        Assert.Equal(NativeHandbackStore.Continuing, Outcome(files, "liveTemporaryCharge"));

        // Second Service process after a clean stop, which ends the managed run.
        File.Delete(Path.Combine(directory, "service-running.json"));
        NativeHandbackStore restarted = StoreFor(files, Bound(record));
        Assert.True(restarted.Load().RequiresRecovery);
        var (context, _) = await StartProductionHandbackAsync(restarted,
            confirmed with { Battery = new ConfirmedBatteryState(true, 80, stillActive) });
        Assert.Equal(stillActive ? NativeHandbackStore.Continuing : "reported", Outcome(files, "liveTemporaryCharge"));

        await ActivateAndTickAsync(context);

        Assert.Equal(stillActive, context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);
        Assert.Equal(stillActive ? "matched" : "reported", Outcome(files, "liveTemporaryCharge"));
        Assert.True(HistoryHas(context, stillActive
            ? "Temporary full charge started by the previous native service continues for the signed-in user."
            : "Temporary full charge started by the previous native service has ended."), Describe(context));
        Assert.Equal(0, context.Device.BatterySetCalls);
        AssertNoHardwareWrite(context);
    }

    // One disagreeing observation is not yet a resolution; channels that agree
    // on the next fresh read resolve normally.
    [Fact]
    public async Task NativeHandback_TransientChannelDisagreementStaysPending()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = state;
        context.Device.ReadStateFailure = ThermalRejection(DeviceStateQuery.ThermalChannelAgreement, state);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Null(Outcome(files, "pending"));
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);

        context.Device.ReadStateFailure = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal("matched", Outcome(files, "pending"));
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    // After a persistent disagreement resolves, neither Reapply nor an explicit
    // thermal setting writes over a pair that still disagrees.
    [Fact]
    public async Task NativeHandback_StillDisagreeingChannelsAreNeverWrittenBlind()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.Capabilities = HandbackCapabilities;
        context.Device.ConfirmedState = state;
        context.Device.ReadStateFailure = ThermalRejection(DeviceStateQuery.ThermalChannelAgreement, state);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true, recoveringManagedRun: false);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal("mismatched", Outcome(files, "pending"));

        await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None);
        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(PerformancePreset.Performance, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);

        Assert.True(context.Device.PerformanceSetCalls == 0 && context.Device.FanWrites.Count == 0 && context.Device.FanTargets.Count == 0,
            "Blind thermal write: " + Describe(context));
    }

    // A persistent disagreement in the production composition resolves before
    // any user, and deferred recovery then completes at activation.
    [Fact]
    public async Task NativeHandback_ProductionDisagreementResolvesAndRecoveryCompletes()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), LightingSettingsFactory.Off());
        var (context, _) = await StartProductionHandbackAsync(StoreFor(files, Bound(record)), state,
            arrange: context => context.Device.ReadStateFailure = ThermalRejection(DeviceStateQuery.ThermalChannelAgreement, state));
        Assert.Null(Outcome(files, "pending"));
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal("mismatched", Outcome(files, "pending"));
        Assert.True(HistoryHas(context, "The laptop's two thermal channels still report different states after the previous native service stopped while changing Performance. OpenBlade left them unchanged; thermal changes cannot be applied until both channels report the same state."));
        await ActivateAndTickAsync(context);
        AssertNoHardwareWrite(context);
        Assert.True(HistoryContains(context, RecoveryCompleted), Describe(context));
    }
    // A temporary charge confirmed before sign-in is an active charge, not a
    // request. If it ended before a restart, either slot reports it ended,
    // with no external-change latch and no battery write. One still active
    // with a different limit has not ended: it is a mismatch with the latch.
    [Theory]
    [InlineData("live", 80, false, "reported")]
    [InlineData("pending-enable", 80, false, "reported")]
    [InlineData("live", 60, true, "mismatched")]
    [InlineData("pending-enable", 60, true, "mismatched")]
    public async Task NativeHandback_ContinuingChargeAtARestartIsReportedOnlyWhenEnded(string kind, int limit, bool temporary, string expected)
    {
        WindowsNativeHandback record = kind == "live"
            ? new(HandbackEpoch, null, null, HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null)
            : PendingHandback(HandbackItem("temporaryCharge", 71, battery: new(80, true, true)));
        string key = kind == "live" ? "liveTemporaryCharge" : "pending";
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var active = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, true), LightingSettingsFactory.Off());
        await StartProductionHandbackAsync(StoreFor(files, Bound(record)), active);
        Assert.Equal(NativeHandbackStore.Continuing, Outcome(files, key));

        File.Delete(Path.Combine(directory, "service-running.json"));
        var (context, _) = await StartProductionHandbackAsync(StoreFor(files, Bound(record)),
            active with { Battery = new ConfirmedBatteryState(true, limit, temporary) });

        Assert.Equal(expected, Outcome(files, key));
        bool ended = expected == "reported";
        Assert.Equal(!ended, context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Battery));
        Assert.Equal(ended, HistoryHas(context, "Temporary full charge started by the previous native service has ended."));
        await ActivateAndTickAsync(context);
        Assert.Equal(expected, Outcome(files, key));
        if (ended)
        {
            Assert.DoesNotContain(DeviceSubsystem.Battery, context.Runtime.GetStatus().ExternalChanges);
            Assert.False(context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);
            Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        }
        Assert.Equal(0, context.Device.BatterySetCalls);
        AssertNoHardwareWrite(context);
    }

    // A pending temporary-charge cancel beside the live charge it was ending is
    // not an active charge alone: whatever the fresh read shows, both items
    // resolve together, and only a read matching the cancel is "matched".
    [Theory]
    [InlineData(60, false, "mismatched")]
    [InlineData(80, true, "mismatched")]
    [InlineData(80, false, "matched")]
    public async Task NativeHandback_PendingCancelBesideALiveChargeResolvesTogether(int limit, bool temporary, string expected)
    {
        WindowsNativeHandback record = new(HandbackEpoch,
            HandbackItem("temporaryCharge", 71, battery: new(80, true, false)), null,
            HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null);
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, limit, temporary), null));
        Assert.Equal(expected, Outcome(files, "pending"));
        Assert.Equal(expected, Outcome(files, "liveTemporaryCharge"));
        Assert.Equal(expected == "mismatched", context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Battery));
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    // Any result between two disagreeing observations restarts the count, so
    // alternating with another unreadable read never resolves; two
    // consecutive disagreements then do.
    [Fact]
    public async Task NativeHandback_DisagreementAlternatingWithAnotherUnreadableReadStaysPending()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), LightingSettingsFactory.Off());
        var (context, _) = await StartProductionHandbackAsync(StoreFor(files, Bound(record)), state,
            arrange: c => c.Device.ReadStateFailure = ThermalRejection(DeviceStateQuery.ThermalChannelAgreement, state));
        for (int cycle = 0; cycle < 6; cycle++)
        {
            context.Device.ReadStateFailure = ThermalRejection(
                cycle % 2 == 0 ? DeviceStateQuery.ThermalChannelOne : DeviceStateQuery.ThermalChannelAgreement, state);
            await context.Runtime.ObserveAsync(CancellationToken.None);
            await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        }
        Assert.Null(Outcome(files, "pending"));
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal("mismatched", Outcome(files, "pending"));
        AssertNoHardwareWrite(context);
    }

    // The resolution names the outcome for user requests; like its record it
    // is private custody and never inherits the namespace's Users read.
    [Fact]
    public void NativeHandback_ResolutionIsPublishedWithPrivateCustody()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        NativeHandbackStore store = HandbackStore(record, out WindowsNativeEpochFiles files);
        NativeHandbackObligation obligation = Assert.Single(store.Load().Obligations);
        Assert.True(store.TryResolve(new(obligation.Epoch, obligation.RecordCommitment, [obligation.Key], null, false), "matched"));

        var resolution = new System.Security.AccessControl.RawSecurityDescriptor(new FileInfo(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch))
            .GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        var root = new System.Security.AccessControl.RawSecurityDescriptor(new DirectoryInfo(files.Root)
            .GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        Assert.Equal(root.Owner, resolution.Owner);
        Assert.True(WindowsNativeEpochFiles.AcceptsPrivateCustody(resolution, root.Owner));
        // Replacing the resolution keeps the private recipe, and it still reads.
        Assert.True(store.TryResolve(new(obligation.Epoch, obligation.RecordCommitment, [obligation.Key], null, false), "matched"));
        Assert.True(WindowsNativeEpochFiles.AcceptsPrivateCustody(new System.Security.AccessControl.RawSecurityDescriptor(
            new FileInfo(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch)).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0), root.Owner));
        Assert.Null(StoreFor(files, Bound(record)).Load().Failure);
    }

    // The production registration: AddServiceControlRuntime registers the
    // store over the handoff binding, and the runtime factory first requests
    // that registered store. The probe stops at that first request and does
    // not run the rest of the factory; the runtime constructor requires the
    // store, so the factory cannot omit it.
    [Fact]
    public void NativeHandback_ServiceControlRuntimeRegistrationUsesTheRegisteredStore()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var services = new ServiceCollection();
        services.AddServiceControlRuntime(Path.Combine(directory, "machine"));
        services.AddSingleton(new ServiceMailboxHandoff(new ManagedMailboxStartup.Admission(null, null, null,
            new NativeHandbackBinding(Bound(record), files.ReopenLater()))));
        using ServiceProvider provider = services.BuildServiceProvider();

        NativeHandbackStore store = provider.GetRequiredService<NativeHandbackStore>();
        Assert.Equal("pending", Assert.Single(store.Load().Obligations).Key);

        ServiceDescriptor runtime = Assert.Single(services, d => d.ServiceType == typeof(IServiceControlRuntime));
        var requests = new List<Type>();
        Assert.Throws<FirstRequestProvider.Stop>(() => runtime.ImplementationFactory!(new FirstRequestProvider(requests)));
        Assert.Equal([typeof(NativeHandbackStore)], requests);
    }

    private sealed class FirstRequestProvider(List<Type> requests) : IServiceProvider
    {
        internal sealed class Stop : Exception;
        public object? GetService(Type serviceType)
        {
            requests.Add(serviceType);
            throw new Stop();
        }
    }

    // The hosted startup is what opens custody: before it the handoff refuses
    // the binding and every transport reservation, after it both are admitted,
    // and custody is never reacquired.
    [Fact]
    public void NativeHandback_HostedCustodyStartupOpensTheHandoff()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var binding = new NativeHandbackBinding(Bound(record), files.ReopenLater());
        var admission = new ManagedMailboxStartup.Admission(new NoMailboxRun(), null, null, binding);
        int opens = 0;
        var handoff = new ServiceMailboxHandoff(() => { opens++; return admission; });
        Assert.Throws<InvalidOperationException>(() => handoff.NativeHandbacks);
        Assert.Throws<IOException>(() => handoff.Lifetime.Reserve());

        _ = new ServiceMailboxCustodyStartup(handoff);

        Assert.Equal(1, opens);
        Assert.Same(binding, handoff.NativeHandbacks);
        handoff.Lifetime.Reserve().Dispose();
        Assert.Throws<IOException>(() => handoff.Open());
        Assert.Equal(1, opens);
    }

    // Admission runs before the transport lifetime is admitted: while custody
    // is being acquired no reservation succeeds, and a refused admission
    // leaves the lifetime refusing, exposes no binding and is never retried.
    [Fact]
    public void NativeHandback_CustodyAdmissionPrecedesTheTransportLifetime()
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(record);
        var admission = new ManagedMailboxStartup.Admission(new NoMailboxRun(), null, null,
            new NativeHandbackBinding(Bound(record), files.ReopenLater()));
        ServiceMailboxHandoff? admitted = null;
        bool reservedDuringAdmission = true;
        admitted = new ServiceMailboxHandoff(() =>
        {
            try { admitted!.Lifetime.Reserve().Dispose(); }
            catch (IOException) { reservedDuringAdmission = false; }
            return admission;
        });
        admitted.Open();
        Assert.False(reservedDuringAdmission);
        admitted.Lifetime.Reserve().Dispose();

        int opens = 0;
        var refused = new ServiceMailboxHandoff(() => { opens++; throw new IOException("custody refused"); });
        Assert.Throws<IOException>(() => new ServiceMailboxCustodyStartup(refused));
        Assert.Throws<IOException>(() => refused.Lifetime.Reserve());
        Assert.Throws<InvalidOperationException>(() => refused.NativeHandbacks);
        Assert.Throws<IOException>(() => refused.Open());
        Assert.Equal(1, opens);
    }
}

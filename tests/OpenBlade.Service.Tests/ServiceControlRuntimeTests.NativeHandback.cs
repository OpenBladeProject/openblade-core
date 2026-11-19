using System.Security.Principal;
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

    // A protected fixture holding the Setup-written records.
    private WindowsNativeEpochFiles HandbackFiles(params WindowsNativeHandback[] records)
    {
        WindowsNativeEpochFiles files = WindowsNativeEpochFiles.CreatePrivateFixture();
        handbackFixtures.Add(files);
        foreach (WindowsNativeHandback record in records)
        {
            files.Publish(WindowsNativeHandbackStorage.RecordPath(files, record.Epoch), record.Serialize([]));
        }
        return files;
    }

    private static NativeHandbackStore StoreFor(WindowsNativeEpochFiles? files, IReadOnlyDictionary<string, string> authenticated) =>
        new(() => files?.RetainView(), () => authenticated);

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

    // Production composition: a user configuration repository is present, so
    // unexpected-exit recovery waits for an authorized active user. This is the
    // ServiceStartupCoordinator sequence after NativeHandbackRecoveryPending.
    private async Task<(TestContext Context, FixedUserConfigurationRepository Repository)> StartProductionHandbackAsync(
        NativeHandbackStore? store, ConfirmedDeviceState confirmed, PowerSource powerSource = PowerSource.PluggedIn,
        bool recoveringManagedRun = false)
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
        BatteryConfiguration machineConfiguration = await context.Store.LoadAsync(CancellationToken.None);
        context.Device.Capabilities = HandbackCapabilities;
        context.Device.ConfirmedState = confirmed;
        context.Device.FanTargetsAvailable = true;
        Assert.Equal(store is not null, context.Runtime.NativeHandbackRecoveryPending);
        context.Runtime.Initialize(machineConfiguration, powerSource, PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true, recoveringManagedRun);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
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
    // a pair that never reads as one state; it resolves as a mismatch for an
    // explicit reapply. Any other thermal read failure keeps waiting.
    [Theory]
    [InlineData("performance", true)]
    [InlineData("fan", true)]
    [InlineData("performance", false)]
    [InlineData("fan", false)]
    public async Task NativeHandback_DisagreeingThermalChannelsResolveAsAMismatch(string family, bool disagreement)
    {
        NativeHandbackItem item = family == "performance"
            ? HandbackItem("performance", 2, performance: "Balanced")
            : HandbackItem("fan", 12, fanMode: "Automatic");
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

        DeviceSubsystem subsystem = family == "performance" ? DeviceSubsystem.Performance : DeviceSubsystem.Fans;
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
    [InlineData("fan", 31)]
    public async Task NativeHandback_UnsentPreparationIsReportedOnly(string family, int progress)
    {
        NativeHandbackItem item = family == "performance"
            ? HandbackItem("performance", progress, performance: "Performance")
            : HandbackItem("fan", progress, fanMode: "Automatic");
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
    [InlineData("temporary-charge", "pending", "matched")]
    [InlineData("live-temporary-charge", "liveTemporaryCharge", "matched")]
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

        Assert.Equal("matched", Outcome(files, "liveTemporaryCharge"));
        Assert.True(HistoryHas(context,
            "Temporary full charge started by the previous native service is still active. OpenBlade continues it for the user who started it when they sign in."));
        Assert.False(context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);

        await ActivateAndTickAsync(context, activeSid);

        Assert.True(HistoryHas(context, message), Describe(context));
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
    // computed the same history commitment for that generation.
    [Theory]
    [InlineData("unbound")]
    [InlineData("other-history")]
    [InlineData("missing-record")]
    [InlineData("missing-directory")]
    [InlineData("two-generations")]
    [InlineData("foreign-resolution")]
    [InlineData("orphan-resolution")]
    [InlineData("resolution-for-absent-item")]
    [InlineData("unknown-entry")]
    public void NativeHandback_StoreRefusesUnboundOrUnknownEvidence(string fault)
    {
        WindowsNativeHandback record = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeHandback later = PendingHandback(HandbackItem("battery", 60, battery: new(80, true, false)), LaterHandbackEpoch);
        WindowsNativeEpochFiles files = fault == "two-generations" ? HandbackFiles(record, later) : HandbackFiles(record);
        IReadOnlyDictionary<string, string> bound = fault switch
        {
            "unbound" => new Dictionary<string, string>(),
            "other-history" => new Dictionary<string, string> { [HandbackEpoch] = later.Commitment },
            "missing-record" or "two-generations" => Bound(record, later),
            _ => Bound(record),
        };
        switch (fault)
        {
            case "foreign-resolution":
                files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch), NativeCanonical.Lines(
                    "OpenBlade.NativeHandbackResolution.v1", HandbackEpoch, new string('b', 64), "matched", "-", "-", "-"));
                break;
            case "orphan-resolution":
                files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, LaterHandbackEpoch), NativeCanonical.Lines(
                    "OpenBlade.NativeHandbackResolution.v1", LaterHandbackEpoch, new string('b', 64), "matched", "-", "-", "-"));
                break;
            // The record holds only a pending item; an outcome for its owned fan is unknown.
            case "resolution-for-absent-item":
                files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, HandbackEpoch), NativeCanonical.Lines(
                    "OpenBlade.NativeHandbackResolution.v1", HandbackEpoch, record.RecordCommitment([]), "-", "reported", "-", "-"));
                break;
            case "unknown-entry":
                files.Publish(Path.Combine(files.Root, "notes.txt"), "x"u8.ToArray());
                break;
        }

        NativeHandbackAdoption adoption = StoreFor(fault == "missing-directory" ? null : files, bound).Load();

        Assert.NotNull(adoption.Failure);
        Assert.Empty(adoption.Obligations);
        Assert.True(adoption.RequiresRecovery);
    }

    [Fact]
    public void NativeHandback_StoreAdmitsBoundRecordsAndResolvedHistory()
    {
        WindowsNativeHandback earlier = PendingHandback(HandbackItem("battery", 60, battery: new(80, true, false)), LaterHandbackEpoch);
        WindowsNativeHandback current = PendingHandback(HandbackItem("performance", 2, performance: "Balanced"));
        WindowsNativeEpochFiles files = HandbackFiles(earlier);
        NativeHandbackStore first = StoreFor(files, Bound(earlier));
        NativeHandbackObligation obligation = Assert.Single(first.Load().Obligations);
        Assert.True(first.TryResolve(new(obligation.Epoch, obligation.RecordCommitment, [obligation.Key], null, false), "matched"));
        files.Publish(WindowsNativeHandbackStorage.RecordPath(files, current.Epoch), current.Serialize([]));

        // A later generation is bound now; the earlier one is fully resolved.
        NativeHandbackAdoption adoption = StoreFor(files, Bound(current)).Load();

        Assert.Null(adoption.Failure);
        Assert.Equal(HandbackEpoch, Assert.Single(adoption.Obligations).Epoch);
        Assert.False(StoreFor(null, new Dictionary<string, string>()).Load().RequiresRecovery);
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
}

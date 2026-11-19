using OpenBlade.Contracts;
using OpenBlade.Deployment;

namespace OpenBlade.Service.Tests;

// A crashed native service hands possibly submitted writes to managed. They are
// confirmed by fresh reads, resolved write-ahead, and never replayed.
public sealed partial class ServiceControlRuntimeTests
{
    private const string HandbackEpoch = "22222222222222222222222222222222";
    private const string HandbackSid = "S-1-5-21-1-2-3-1001";

    private static NativeHandbackItem HandbackItem(string family, int progress, string? performance = null,
        string? fanMode = null, NativeHandbackBattery? battery = null) =>
        new("aaaaaaaa-bbbb-cccc-dddd-000000000001", family, progress, HandbackSid, performance, fanMode, battery);

    private NativeHandbackStore HandbackStore(WindowsNativeHandback handback, out string handbackDirectory)
    {
        handbackDirectory = Path.Combine(directory, WindowsNativeHandbackWriter.DirectoryName);
        Directory.CreateDirectory(handbackDirectory);
        File.WriteAllBytes(Path.Combine(handbackDirectory, handback.Epoch + ".json"), handback.Serialize([]));
        return new NativeHandbackStore(handbackDirectory);
    }

    private async Task<TestContext> StartWithHandbackAsync(NativeHandbackStore store, ConfirmedDeviceState confirmed)
    {
        TestContext context = CreateContext(providerAvailable: true, initialize: false, nativeHandbackStore: store);
        context.Device.ConfirmedState = confirmed;
        Assert.True(context.Runtime.NativeHandbackRecoveryPending);
        context.Runtime.Initialize(await context.Store.LoadAsync(CancellationToken.None), PowerSource.PluggedIn,
            PersistentOverrideState.Empty, recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        return context;
    }

    private static string Resolutions(string handbackDirectory) =>
        File.ReadAllText(Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile));

    private static bool HistoryContains(TestContext context, string text) =>
        context.History.Snapshot().CommandResults.Any(r => r.Detail?.Contains(text, StringComparison.Ordinal) == true
            || r.Action.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task NativeHandback_MatchingFirmwareResolvesWriteAheadAndIsNotReseeded()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, HandbackItem("performance", 2, performance: "Balanced"), null, null, null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains("\"Outcome\":\"matched\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
        // Consumed: a restart neither reseeds nor re-enters recovery.
        Assert.False(new NativeHandbackStore(handbackDirectory).Load().RequiresRecovery);
        Assert.True(File.Exists(Path.Combine(handbackDirectory, HandbackEpoch + ".json")));
    }

    [Fact]
    public async Task NativeHandback_DifferentFirmwareStateIsLatchedAndNeverRewritten()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, HandbackItem("performance", 4, performance: "Performance"), null, null, null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Silent, FanMode.Automatic, ConfirmedBattery(80), null));
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Contains("\"Outcome\":\"mismatched\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task NativeHandback_UnreadableFirmwareKeepsThermalWritesBlocked()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, HandbackItem("performance", 2, performance: "Balanced"), null, null, null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(null, FanMode.Automatic, ConfirmedBattery(80), null));

        ControlOperationResult reapply = await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, reapply.Status);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.False(File.Exists(Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile)));
        Assert.True(new NativeHandbackStore(handbackDirectory).Load().RequiresRecovery);
    }

    [Fact]
    public async Task NativeHandback_UnpersistedResolutionStaysPendingUntilItCanBeWritten()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, HandbackItem("performance", 2, performance: "Balanced"), null, null, null, null), out string handbackDirectory);
        string blocker = Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile + ".tmp");
        Directory.CreateDirectory(blocker);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));

        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None)).Status);
        Assert.False(File.Exists(Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile)));

        Directory.Delete(blocker);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Contains("\"Outcome\":\"matched\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task NativeHandback_UnreadableRecordBlocksWithoutGuessing()
    {
        string handbackDirectory = Path.Combine(directory, WindowsNativeHandbackWriter.DirectoryName);
        Directory.CreateDirectory(handbackDirectory);
        File.WriteAllText(Path.Combine(handbackDirectory, HandbackEpoch + ".json"), "{");
        TestContext context = await StartWithHandbackAsync(new NativeHandbackStore(handbackDirectory),
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null));

        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.ReapplyChangedSettingsAsync(CancellationToken.None)).Status);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.False(File.Exists(Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile)));
        Assert.True(HistoryContains(context, "could not be read"));
    }

    [Fact]
    public async Task NativeHandback_InheritedManualFanIsReportedAndLeftToManualTopology()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, null, HandbackItem("fan", 30, fanMode: "Fixed"), null, null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Fixed, ConfirmedBattery(80), null));

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Contains("\"Key\":\"ownedFan\",\"Outcome\":\"reported\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
        Assert.True(HistoryContains(context, "reapply your fan setting"));
        Assert.False(new NativeHandbackStore(handbackDirectory).Load().RequiresRecovery);
    }

    [Fact]
    public async Task NativeHandback_LiveTemporaryChargeIsConfirmedAsTheNativeOverride()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, null, null,
            HandbackItem("temporaryCharge", 72, battery: new(80, true, true)), null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, new ConfirmedBatteryState(true, 80, true), null));

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Contains("\"Key\":\"liveTemporaryCharge\",\"Outcome\":\"matched\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceSubsystem.Battery, context.Runtime.GetStatus().ExternalChanges);
        Assert.True(HistoryContains(context, "Temporary full charge started by the previous native service is still active"));
    }

    [Fact]
    public async Task NativeHandback_PowerSourceChangeDoesNotDischargeTheObligation()
    {
        NativeHandbackStore store = HandbackStore(new(HandbackEpoch, HandbackItem("performance", 2, performance: "Balanced"), null, null, null, null), out string handbackDirectory);
        TestContext context = await StartWithHandbackAsync(store,
            new ConfirmedDeviceState(null, FanMode.Automatic, ConfirmedBattery(80), null));
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(handbackDirectory, NativeHandbackStore.ResolutionFile)));
        Assert.Equal(0, context.Device.PerformanceSetCalls);

        context.Device.ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, ConfirmedBattery(80), null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Contains("\"Outcome\":\"matched\"", Resolutions(handbackDirectory), StringComparison.Ordinal);
    }
}

using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    private async Task<TestContext> CreateLocalDimmingContextAsync(LocalDimmingCapabilities? capabilities)
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with { LocalDimming = capabilities };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { LocalDimmingActive = true };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task LocalDimmingClear_AppliesOnceAndPublishesTheConfirmedOffState()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        Assert.True(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.LocalDimmingClearCalls);
        Assert.False(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    public static TheoryData<LocalDimmingCapabilities?> UnadmittedLocalDimming() =>
    [
        null,
        new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, SupportsClear: false),
        new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: false),
    ];

    [Theory]
    [MemberData(nameof(UnadmittedLocalDimming))]
    public async Task LocalDimmingClear_WithoutAdmittedClearNeverReachesTheDevice(LocalDimmingCapabilities? capabilities)
    {
        TestContext context = await CreateLocalDimmingContextAsync(capabilities);
        using ServiceControlRuntime runtime = context.Runtime;

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Device.LocalDimmingClearCalls);
    }

    [Fact]
    public async Task LocalDimmingClear_KnownNotAppliedRefusalKeepsWritesAvailable()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.LocalDimmingClearFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied, "Turn them off first.");

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal("Turn them off first.", result.Message);
        Assert.Equal(1, context.Device.LocalDimmingClearCalls);
        Assert.False(runtime.GetStatus().WritesBlocked);
        // A refusal changed nothing: the last confirmed reading stays.
        Assert.True(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);
    }

    [Fact]
    public async Task LocalDimmingClear_UncertainResultFailsWithoutRetryAndForgetsTheState()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.LocalDimmingClearFailure = new BladeWriteException(
            WriteSubmissionState.Indeterminate, "Readback did not match.");

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(1, context.Device.LocalDimmingClearCalls);
        Assert.Null(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);
    }

    [Fact]
    public async Task LocalDimmingClear_CapabilityRefusalKeepsTheConfirmedReading()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.LocalDimmingClearFailure = new CapabilityUnavailableException("Local dimming Clear is not admitted.");

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.True(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalDimmingClear_FailureMentionsResumeOnlyWhenWritesArePaused(bool paused)
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.DuringLocalDimmingClear = () => context.Provider.IsExperimentalWritesPaused = paused;
        context.Device.LocalDimmingClearFailure = paused
            ? new BladeWriteException(WriteSubmissionState.Indeterminate, "Readback did not match.")
            : new IOException("The device stopped responding.");

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(paused, result.Message.Contains("Resume laptop controls", StringComparison.Ordinal));
        Assert.Equal(paused, result.Message.Contains("paused", StringComparison.Ordinal));
        Assert.Null(runtime.GetStatus().ConfirmedState!.LocalDimmingActive);
    }

    // One test per admission condition, each with every other condition passing.
    [Fact]
    public async Task LocalDimmingClear_RequiresAVerifiedDevice()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Provider.Available = false;
        await runtime.ObserveAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.False(runtime.GetStatus().DeviceVerified);
        // The device answers again, but no observation has re-verified it yet.
        context.Provider.Available = true;

        await AssertLocalDimmingClearBlockedAsync(context);
    }

    [Fact]
    public async Task LocalDimmingClear_RequiresWritesNotBlocked()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Provider.IsExperimentalWritesPaused = true;
        Assert.True(runtime.GetStatus().WritesBlocked);

        await AssertLocalDimmingClearBlockedAsync(context);
    }

    [Fact]
    public async Task LocalDimmingClear_RequiresTheLaptopToBeAwake()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.LowPowerGate.Enter();

        await AssertLocalDimmingClearBlockedAsync(context);
    }

    [Fact]
    public async Task LocalDimmingClear_RequiresTheActiveUserToStillControlTheSession()
    {
        var user = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet> { [user.Value] = ProfileDefaults.CreateSafeDefaults() }, directory);
        TestContext context = CreateWritableContext(userConfigurationRepository: repository);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LocalDimming = new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { LocalDimmingActive = true };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        bool controlsSession = true;
        await context.Runtime.ActivateUserAsync(user, () => controlsSession, CancellationToken.None);
        using ServiceControlRuntime runtime = context.Runtime;
        Assert.Equal(ControlOperationStatus.Applied,
            (await runtime.ClearLocalDimmingAsync(CancellationToken.None)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { LocalDimmingActive = true };
        await runtime.ObserveAsync(CancellationToken.None);
        int calls = context.Device.LocalDimmingClearCalls;
        controlsSession = false;

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(calls, context.Device.LocalDimmingClearCalls);
    }

    // The admission reads the Windows power source afresh; an unreadable source
    // stops the Clear before the device.
    [Fact]
    public async Task LocalDimmingClear_RequiresAFreshPowerSourceRead()
    {
        TestContext context = await CreateLocalDimmingContextAsync(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true));
        using ServiceControlRuntime runtime = context.Runtime;
        context.Telemetry.PowerCaptureFailure = new IOException("Power source is unavailable.");

        await Assert.ThrowsAnyAsync<Exception>(() => runtime.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(0, context.Device.LocalDimmingClearCalls);
    }

    [Fact]
    public async Task LocalDimmingClear_FreshUnclassifiedExternalPowerIsAdmitted()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LocalDimming = new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { LocalDimmingActive = true };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        using ServiceControlRuntime runtime = context.Runtime;

        ControlOperationResult result = await runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.LocalDimmingClearCalls);
    }

    private static async Task AssertLocalDimmingClearBlockedAsync(TestContext context)
    {
        ControlOperationResult result = await context.Runtime.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal("Local dimming can't be turned off right now.", result.Message);
        Assert.Equal(0, context.Device.LocalDimmingClearCalls);
    }
}

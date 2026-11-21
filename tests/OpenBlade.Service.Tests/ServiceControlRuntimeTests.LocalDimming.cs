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
}

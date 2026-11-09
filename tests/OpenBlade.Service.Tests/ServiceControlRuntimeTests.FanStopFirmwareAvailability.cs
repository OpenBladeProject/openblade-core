using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirmwarePreflightBlocksAutomaticLaunchButPreservesEvidenceAndRecovers(bool unknown)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        context.Device.Capabilities = context.Device.Capabilities with
        { HasFanStopTrialEvidence = false, HasFanStopSourceEvidence = true };
        var family = FanStopModelContract.ForModel("RZ09-0483")!;
        Version? version = unknown ? null : new Version(2, 2);
        var probe = new FanStopFirmwareAvailability(() => family,
            _ => FanStopFirmwareAvailabilityTests.Metadata(family, version), clock);
        owner.FirmwareAvailability = () => probe.Read();
        await context.Runtime.ObserveAsync(default);
        await SampleAutomaticFanStopAsync(context, clock, 6);
        var unavailable = context.Runtime.GetStatus();
        Assert.True(unavailable.Capabilities!.HasFanStopSourceEvidence);
        Assert.False(unavailable.Capabilities.HasFanStopTrialEvidence);
        Assert.False(unavailable.Capabilities.SupportsFanStop);
        Assert.False(unavailable.FanStopStatus.CanStop);
        Assert.Equal(FanStopState.Unavailable, unavailable.FanStopStatus.State);
        Assert.Equal(0, owner.StartCalls);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default)).Status);
        Assert.Equal(0, owner.StartCalls);

        version = family.EcVersion;
        clock.Advance(FanStopFirmwareAvailability.RefreshInterval);
        await SampleAutomaticFanStopAsync(context, clock, 3);
        Assert.Equal(1, owner.StartCalls);
        Assert.True(context.Runtime.GetStatus().Capabilities!.HasFanStopSourceEvidence);
        version = null;
        _ = probe.Read(refresh: true);
        Assert.True(context.Runtime.GetStatus().Capabilities!.SupportsFanStop);
        Assert.True(context.Runtime.GetStatus().FanStopStatus.CanReturnToAutomatic);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default)).Status);
        Assert.Equal(1, owner.AutoCalls);
    }
}

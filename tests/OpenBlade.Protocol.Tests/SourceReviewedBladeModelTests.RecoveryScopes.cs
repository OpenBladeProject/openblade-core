using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void RecoveryScope_CannotNarrowBatteryOrUnknownObligation(int strictScope)
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x028C));
        Assert.Equal(ExperimentalRecoveryScope.All, policy.RecoveryScope);
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        Assert.Equal(ExperimentalRecoveryScope.ThermalOnly, policy.RecoveryScope);
        policy.RequireRecovery((ExperimentalRecoveryScope)strictScope);
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        Assert.Equal(ExperimentalRecoveryScope.All, policy.RecoveryScope);
        bool accepted = false;
        Assert.False(policy.TryCompleteRecovery(ExperimentalRecoveryScope.ThermalOnly, () =>
        {
            accepted = true;
            return true;
        }));
        Assert.False(accepted);
        Assert.True(policy.IsPaused);
        Assert.False(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All, () => false));
        Assert.True(policy.IsPaused);
        Assert.True(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All, () => true));
        Assert.False(policy.IsPaused);
        Assert.Equal(ExperimentalRecoveryScope.All, policy.RecoveryScope);
    }

    [Fact]
    public void RecoveryScope_ReentrantWideningDuringAcceptanceRetainsPause()
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x028C));
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        Assert.False(policy.TryCompleteRecovery(ExperimentalRecoveryScope.ThermalOnly, () =>
        {
            policy.RequireRecovery();
            return true;
        }));
        Assert.True(policy.IsPaused);
        Assert.Equal(ExperimentalRecoveryScope.All, policy.RecoveryScope);
    }

    [Fact]
    public async Task ThermalRecovery_OverrideCapableBackendStillRequiresActualBatteryPolicy()
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        var session = new Session { RejectBatteryQuery = true };
        await using var port = Create(0x02A0, session, policy);
        Assert.True(port.SupportsTemporaryFullChargeQuery);
        await Assert.ThrowsAnyAsync<IOException>(() => port.ReadRecoveryStateAsync(default));
        Assert.Contains(session.Commands, command => command.ClassId == 0x07 && command.CommandId == 0x92);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.True(policy.IsPaused);
    }
}

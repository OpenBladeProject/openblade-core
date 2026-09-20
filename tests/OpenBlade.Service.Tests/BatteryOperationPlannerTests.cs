using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class BatteryOperationPlannerTests
{
    [Fact]
    public void DesiredState_CancelsFullChargeBeforeChangingProtection()
    {
        var device = AdmittedDevice();
        var desired = new DesiredBatteryState(BatteryPolicy.Create(75), false, false);
        BatteryOperationPlan plan = BatteryOperationPlanner.CreateBatteryPlan(
            desired, device.Capabilities, new ConfirmedBatteryState(true, 80, true));

        Assert.Equal(new BatteryOperation.TemporaryFullCharge(false), plan.Operation);
        Assert.True(plan.Matches(new ConfirmedBatteryState(true, 80, false)));
        Assert.False(plan.Matches(new ConfirmedBatteryState(true, 75, false)));
        Assert.Empty(device.BatterySetterCalls);
    }

    [Fact]
    public void DesiredState_UnknownFullChargeBlocksEvenMatchingProtectionAndLimit()
    {
        var device = AdmittedDevice();
        BatteryOperationPlan plan = BatteryOperationPlanner.CreateBatteryPlan(
            new DesiredBatteryState(BatteryPolicy.Create(80), true, false),
            device.Capabilities, new ConfirmedBatteryState(true, 80, null));

        Assert.False(plan.Supported);
        Assert.IsType<BatteryOperation.None>(plan.Operation);
        Assert.False(plan.Matches(new ConfirmedBatteryState(true, 80, false)));
    }

    [Fact]
    public void EnableProtection_UsesStoredPolicyAndRequiresExactReadback()
    {
        var device = AdmittedDevice();
        var policy = BatteryPolicy.Create(75);
        BatteryOperationPlan plan = BatteryOperationPlanner.CreateProtectionPlan(
            true, policy, device.Capabilities, new ConfirmedBatteryState(false, null, false));

        Assert.Equal(new BatteryOperation.ChargeLimit(policy), plan.Operation);
        Assert.True(plan.Matches(new ConfirmedBatteryState(true, 75, false)));
        Assert.False(plan.Matches(new ConfirmedBatteryState(true, 80, false)));
        Assert.False(plan.Matches(new ConfirmedBatteryState(true, 75, null)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Transition_CancellationFollowUpRequiresConfirmedCancellation(
        bool confirm, bool loseConfirmation)
    {
        var device = AdmittedDevice();
        device.State = device.State with { Battery = new ConfirmedBatteryState(true, 80, true) };
        device.AutoConfirmWrites = confirm;
        if (loseConfirmation)
        {
            device.AfterSetter = _ => device.State = device.State with { Battery = null };
        }

        await new BatteryOperationExecutor(device).ApplyTransitionAsync(
            new DesiredBatteryState(BatteryPolicy.Create(75), true, false),
            device.Capabilities, device.State.Battery, CancellationToken.None);

        Assert.Equal(confirm && !loseConfirmation
            ? new[] { "SetTemporaryFullCharge", "SetChargeLimit" }
            : new[] { "SetTemporaryFullCharge" }, device.BatterySetterCalls);
        Assert.Equal(1, device.StateReadCalls);
    }

    [Fact]
    public async Task Transition_CancellationFollowUpRemainsBoundedWhenDesiredProtectionCannotBeApplied()
    {
        var device = AdmittedDevice();
        device.State = device.State with { Battery = new ConfirmedBatteryState(true, 80, true) };

        await new BatteryOperationExecutor(device).ApplyTransitionAsync(
            new DesiredBatteryState(BatteryPolicy.Create(75), false, false),
            device.Capabilities, device.State.Battery, CancellationToken.None);

        Assert.Equal(new[] { "SetTemporaryFullCharge" }, device.BatterySetterCalls);
        Assert.Equal(new ConfirmedBatteryState(true, 80, false), device.State.Battery);
    }

    private static BatteryTestDevice AdmittedDevice()
    {
        var device = new BatteryTestDevice();
        device.Capabilities = device.Capabilities with
        {
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        return device;
    }
}

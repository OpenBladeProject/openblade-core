using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ProfileContractTests
{
    [Fact]
    public void DesiredBatteryState_SeparatesConfiguredPolicyFromTransientIntent()
    {
        BatteryPolicy policy = BatteryPolicy.Create(80);

        var state = new DesiredBatteryState(
            policy,
            protectionEnabled: true,
            temporaryFullCharge: false);

        Assert.Equal(policy, state.ConfiguredPolicy);
        Assert.True(state.ProtectionEnabled);
        Assert.False(state.TemporaryFullCharge);
    }

    [Fact]
    public void DesiredBatteryState_RejectsTemporaryFullChargeWhileProtectionIsOff()
    {
        Assert.Throws<ArgumentException>(() =>
            new DesiredBatteryState(
                BatteryPolicy.Create(80),
                protectionEnabled: false,
                temporaryFullCharge: true));
    }

    [Fact]
    public void DesiredBatteryState_RejectsMissingConfiguredPolicy()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DesiredBatteryState(
                null!,
                protectionEnabled: true,
                temporaryFullCharge: false));
    }

    [Fact]
    public void ConfirmedBatteryState_RepresentsExactEnabledReadback()
    {
        var state = new ConfirmedBatteryState(
            protectionEnabled: true,
            chargeLimitPercent: 80,
            temporaryFullCharge: false);

        Assert.True(state.ProtectionEnabled);
        Assert.Equal(80, state.ChargeLimitPercent);
        Assert.False(state.TemporaryFullCharge);
    }

    [Fact]
    public void ConfirmedBatteryState_RejectsEnabledReadbackWithoutLimit()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConfirmedBatteryState(
                protectionEnabled: true,
                chargeLimitPercent: null,
                temporaryFullCharge: null));
    }

    [Fact]
    public void ConfirmedBatteryState_RejectsDisabledReadbackWithLimit()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConfirmedBatteryState(
                protectionEnabled: false,
                chargeLimitPercent: 80,
                temporaryFullCharge: null));
    }

    [Fact]
    public void ConfirmedBatteryState_RejectsTemporaryFullChargeWhileProtectionIsOff()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConfirmedBatteryState(
                protectionEnabled: false,
                chargeLimitPercent: null,
                temporaryFullCharge: true));
    }
}

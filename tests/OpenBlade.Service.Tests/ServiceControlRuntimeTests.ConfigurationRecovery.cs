using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task ActivateUserAsync_RecoveryRequiredLoadRetainsActiveIntentAndConfirmedState()
    {
        string machineRoot = Path.Combine(directory, "configuration-recovery");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(firstSid, CancellationToken.None);
        ProfileSet firstProfiles = first.Configuration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        await first.Store.SaveProfilesAsync(firstProfiles, CancellationToken.None);
        await repository.LoadAsync(secondSid, CancellationToken.None);
        TestContext context = CreateContext(
            providerAvailable: true, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        Assert.Equal(firstProfiles, before.Profiles);
        Assert.NotNull(before.ConfirmedState);
        int setterCalls = context.Device.TotalSetterCalls;
        string recoveryPath = Path.Combine(machineRoot, "users", secondSid.Value,
            "battery-configuration.commit.json.recovery-required");
        await File.WriteAllTextAsync(recoveryPath, "unresolved transaction");

        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None));

        ServiceStatusSnapshot retained = context.Runtime.GetStatus();
        Assert.Equal(before.Profiles, retained.Profiles);
        Assert.Equal(before.DesiredState, retained.DesiredState);
        Assert.Equal(before.ConfirmedState, retained.ConfirmedState);
        Assert.Equal(before.BatteryPolicyOverride, retained.BatteryPolicyOverride);
        Assert.Equal(before.FanControlOverride, retained.FanControlOverride);
        Assert.Equal(before.LightingOverride, retained.LightingOverride);
        Assert.Equal(setterCalls, context.Device.TotalSetterCalls);
        // The failed load did not switch the active user or turn a later A
        // activation into a second hardware application.
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        Assert.Equal(setterCalls, context.Device.TotalSetterCalls);
        Assert.Equal("unresolved transaction", await File.ReadAllTextAsync(recoveryPath));
    }
}

using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class UserConfigurationRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.UserConfigurationRepositoryTests",
        Guid.NewGuid().ToString("N"));
    private readonly ProfileSetValidator validator =
        ProfileSetValidator.CreateUnverifiedSafeDefaults();

    [Fact]
    public async Task LoadAsync_IsolatesProfilesByUserSid()
    {
        Directory.CreateDirectory(directory);
        var repository = new UserConfigurationRepository(directory, validator);
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(
            firstSid,
            CancellationToken.None);
        ProfileSet changed = first.Configuration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        await first.Store.SaveProfilesAsync(changed, CancellationToken.None);

        UserConfigurationContext second = await repository.LoadAsync(
            secondSid,
            CancellationToken.None);

        Assert.Equal(75, (await repository.LoadAsync(
            firstSid,
            CancellationToken.None)).Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.NotEqual(
            75,
            second.Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyConfigurationOnlyForLegacyOwner()
    {
        Directory.CreateDirectory(directory);
        var legacyStore = new BatteryConfigurationStore(
            Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "battery-preferences.json"),
            Path.Combine(directory, "battery-configuration.commit.json"),
            validator);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        await legacyStore.SaveConfigurationAsync(
            profiles,
            new BatteryAutomationPreferences(ProtectionEnabled: true),
            CancellationToken.None);
        var legacyOverrides = new PersistentOverrideStore(
            Path.Combine(directory, "manual-overrides.json"));
        await legacyOverrides.SaveAsync(
            PersistentOverrideState.Empty with
            {
                Performance = PerformancePreset.Silent,
            },
            CancellationToken.None);
        var ownerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "authorized-user.sid"),
            ownerSid.Value);

        var repository = new UserConfigurationRepository(directory, validator);
        UserConfigurationContext migrated = await repository.LoadAsync(
            ownerSid,
            CancellationToken.None);
        UserConfigurationContext anotherUser = await repository.LoadAsync(
            new SecurityIdentifier("S-1-5-21-1-2-3-1002"),
            CancellationToken.None);

        Assert.Equal(75, migrated.Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(PerformancePreset.Silent, migrated.Overrides.Performance);
        Assert.NotEqual(
            75,
            anotherUser.Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Null(anotherUser.Overrides.Performance);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

using System.Security.Principal;

namespace OpenBlade.Service;

public sealed record UserConfigurationContext(
    BatteryConfigurationStore Store,
    PersistentOverrideStore OverrideStore,
    BatteryConfiguration Configuration,
    PersistentOverrideState Overrides);

public interface IUserConfigurationRepository
{
    Task<UserConfigurationContext> LoadAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken);
}

public sealed class UserConfigurationRepository : IUserConfigurationRepository
{
    private readonly string machineDataDirectory;
    private readonly ProfileSetValidator validator;
    private readonly SemaphoreSlim migrationGate = new(1, 1);

    public UserConfigurationRepository(
        string machineDataDirectory,
        ProfileSetValidator validator)
    {
        this.machineDataDirectory = string.IsNullOrWhiteSpace(machineDataDirectory)
            ? throw new ArgumentException(
                "A machine data directory is required.",
                nameof(machineDataDirectory))
            : Path.GetFullPath(machineDataDirectory);
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<UserConfigurationContext> LoadAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        if (!userSid.IsAccountSid())
        {
            throw new ArgumentException("A Windows account SID is required.", nameof(userSid));
        }

        string userDirectory = GetUserDirectory(userSid);
        var store = new BatteryConfigurationStore(
            Path.Combine(userDirectory, "profiles.json"),
            Path.Combine(userDirectory, "battery-preferences.json"),
            Path.Combine(userDirectory, "battery-configuration.commit.json"),
            validator);
        var overrides = new PersistentOverrideStore(
            Path.Combine(userDirectory, "manual-overrides.json"));

        await migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(userDirectory);
            await MigrateLegacyOwnerAsync(
                userSid,
                store,
                overrides,
                cancellationToken).ConfigureAwait(false);

            BatteryConfiguration configuration = await store
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            PersistentOverrideLoadResult loadedOverrides = await overrides
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return new UserConfigurationContext(
                store,
                overrides,
                configuration,
                loadedOverrides.Overrides);
        }
        finally
        {
            migrationGate.Release();
        }
    }

    private string GetUserDirectory(SecurityIdentifier userSid)
    {
        string usersDirectory = Path.Combine(machineDataDirectory, "users");
        string candidate = Path.GetFullPath(Path.Combine(usersDirectory, userSid.Value));
        string expectedPrefix = Path.TrimEndingDirectorySeparator(usersDirectory)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The user configuration path escaped its root.");
        }

        return candidate;
    }

    private async Task MigrateLegacyOwnerAsync(
        SecurityIdentifier userSid,
        BatteryConfigurationStore destination,
        PersistentOverrideStore destinationOverrides,
        CancellationToken cancellationToken)
    {
        string migrationMarker = Path.Combine(
            machineDataDirectory,
            "users",
            userSid.Value,
            ".legacy-migration-complete");
        if (File.Exists(migrationMarker))
        {
            return;
        }

        string ownerPath = Path.Combine(machineDataDirectory, "authorized-user.sid");
        if (!File.Exists(ownerPath)
            || !string.Equals(
                (await File.ReadAllTextAsync(ownerPath, cancellationToken)
                    .ConfigureAwait(false)).Trim(),
                userSid.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var source = new BatteryConfigurationStore(
            Path.Combine(machineDataDirectory, "profiles.json"),
            Path.Combine(machineDataDirectory, "battery-preferences.json"),
            Path.Combine(machineDataDirectory, "battery-configuration.commit.json"),
            validator);
        BatteryConfiguration legacy = await source
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await destination.SaveConfigurationAsync(
            legacy.Profiles,
            legacy.Preferences,
            cancellationToken).ConfigureAwait(false);

        var sourceOverrides = new PersistentOverrideStore(
            Path.Combine(machineDataDirectory, "manual-overrides.json"));
        PersistentOverrideLoadResult legacyOverrides = await sourceOverrides
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await destinationOverrides.SaveAsync(
            legacyOverrides.Overrides,
            cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAsync(
            migrationMarker,
            "migrated"u8.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }
}

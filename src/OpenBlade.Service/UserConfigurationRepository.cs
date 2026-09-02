using System.Security.Principal;

namespace OpenBlade.Service;

public sealed record UserConfigurationContext(
    BatteryConfigurationStore Store,
    PersistentOverrideStore OverrideStore,
    BatteryConfiguration Configuration,
    PersistentOverrideState Overrides,
    ConfigurationLoadDisposition OverrideDisposition =
        ConfigurationLoadDisposition.Admitted,
    ConfigurationQuarantineReason OverrideQuarantineReason =
        ConfigurationQuarantineReason.None)
{
    internal ThermalConvergenceStateStore? ThermalConvergenceStore { get; init; }

    internal ThermalConvergenceLoadResult? ThermalConvergence { get; init; }

    public ConfigurationLoadDisposition Disposition =>
        Configuration.Disposition == ConfigurationLoadDisposition.Admitted
        && OverrideDisposition == ConfigurationLoadDisposition.Admitted
            ? ConfigurationLoadDisposition.Admitted
            : ConfigurationLoadDisposition.Quarantined;

    public ConfigurationQuarantineReason QuarantineReason =>
        Configuration.Disposition == ConfigurationLoadDisposition.Quarantined
            ? Configuration.QuarantineReason
            : OverrideQuarantineReason;
}

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
        var thermalConvergence = new ThermalConvergenceStateStore(
            Path.Combine(userDirectory, "thermal-convergence.json"),
            userSid);

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
            ThermalConvergenceLoadResult loadedThermalConvergence =
                await thermalConvergence
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            return new UserConfigurationContext(
                store,
                overrides,
                configuration,
                loadedOverrides.Overrides,
                loadedOverrides.Disposition,
                loadedOverrides.QuarantineReason)
            {
                ThermalConvergenceStore = thermalConvergence,
                ThermalConvergence = loadedThermalConvergence,
            };
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
        BatteryConfigurationInspection legacy = await source
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        var sourceOverrides = new PersistentOverrideStore(
            Path.Combine(machineDataDirectory, "manual-overrides.json"));
        PersistentOverrideInspection legacyOverrides = await sourceOverrides
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);
        BatteryConfigurationInspection destinationConfiguration = await destination
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);
        PersistentOverrideInspection destinationOverride = await destinationOverrides
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!legacy.Configuration.IsAuthoritative
            || legacyOverrides.Result.Disposition ==
                ConfigurationLoadDisposition.Quarantined
            || !destinationConfiguration.Configuration.IsAuthoritative
            || destinationOverride.Result.Disposition ==
                ConfigurationLoadDisposition.Quarantined)
        {
            return;
        }

        bool sourceHasConfiguration = !legacy.WasEmpty;
        bool sourceHasOverrides = !legacyOverrides.WasEmpty;
        if (!sourceHasConfiguration && !sourceHasOverrides)
        {
            await AtomicFile.WriteAsync(
                migrationMarker,
                "migrated"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (destinationConfiguration.WasEmpty && sourceHasConfiguration)
        {
            await destination.SaveConfigurationAsync(
                legacy.Configuration.Profiles,
                legacy.Configuration.Preferences,
                cancellationToken).ConfigureAwait(false);
        }
        if (destinationOverride.WasEmpty && sourceHasOverrides)
        {
            await destinationOverrides.SaveAsync(
                legacyOverrides.Result.Overrides,
                cancellationToken).ConfigureAwait(false);
        }

        await AtomicFile.WriteAsync(
            migrationMarker,
            "migrated"u8.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }
}

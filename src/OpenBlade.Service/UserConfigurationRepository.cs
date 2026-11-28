using System.ComponentModel;
using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record UserConfigurationContext(
    BatteryConfigurationStore Store,
    PersistentOverrideStore OverrideStore,
    BatteryConfiguration Configuration,
    PersistentOverrideState Overrides,
    ConfigurationLoadDisposition OverrideDisposition =
        ConfigurationLoadDisposition.Admitted,
    ConfigurationQuarantineReason OverrideQuarantineReason =
        ConfigurationQuarantineReason.None) : IAsyncDisposable
{
    internal UserConfigurationWriterScope? WriterScope { get; init; }

    public ValueTask DisposeAsync() => WriterScope?.DisposeAsync() ?? ValueTask.CompletedTask;

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
    private readonly Func<string, CancellationToken, IDisposable>? acquireVerifiedWriterLease;
    private readonly Func<CancellationToken, Task>? beforeOverrideWrite;

    internal bool UsesWriterLease => acquireVerifiedWriterLease is not null;

    public UserConfigurationRepository(
        string machineDataDirectory,
        ProfileSetValidator validator)
        : this(machineDataDirectory, validator, acquireVerifiedWriterLease: null)
    {
    }

    // The injected verifier must pin this exact, already provisioned user root
    // and its shared exclusive lock. Production composition does not supply it.
    internal UserConfigurationRepository(
        string machineDataDirectory,
        ProfileSetValidator validator,
        Func<string, CancellationToken, IDisposable>? acquireVerifiedWriterLease,
        Func<CancellationToken, Task>? beforeOverrideWrite = null)
    {
        this.acquireVerifiedWriterLease = acquireVerifiedWriterLease;
        this.beforeOverrideWrite = beforeOverrideWrite;
        this.machineDataDirectory = string.IsNullOrWhiteSpace(machineDataDirectory)
            ? throw new ArgumentException(
                "A machine data directory is required.",
                nameof(machineDataDirectory))
            : Path.GetFullPath(machineDataDirectory);
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public Task<UserConfigurationContext> LoadAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken) =>
        LoadAsync(userSid, existingScope: null, cancellationToken);

    // An interrupted activation reloads and migrates under its existing handle.
    // The runtime serializes the reload and retains ownership of that scope.
    internal async Task<UserConfigurationContext> LoadAsync(
        SecurityIdentifier userSid,
        UserConfigurationWriterScope? existingScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        if (!userSid.IsAccountSid())
        {
            throw new ArgumentException("A Windows account SID is required.", nameof(userSid));
        }

        string userDirectory = GetUserDirectory(userSid);
        string markerPath = Path.Combine(userDirectory, "battery-configuration.commit.json");
        await migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        UserConfigurationWriterScope? ownedScope = null;
        try
        {
            if (existingScope is not null)
            {
                existingScope.CheckRoot(markerPath);
            }
            else if (acquireVerifiedWriterLease is null)
            {
                Directory.CreateDirectory(userDirectory);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                IDisposable lease;
                try
                {
                    lease = acquireVerifiedWriterLease(userDirectory, cancellationToken)
                        ?? throw new InvalidOperationException("The verified writer lease was not supplied.");
                }
                catch (Win32Exception exception)
                {
                    throw new IOException("Could not acquire the verified user configuration writer lease.", exception);
                }
                ownedScope = new UserConfigurationWriterScope(lease, markerPath);
            }

            UserConfigurationWriterScope? writerScope = existingScope ?? ownedScope;
            if (writerScope is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BatteryConfigurationStore.CheckRecoveryRequired(markerPath);
            }

            var store = new BatteryConfigurationStore(
                Path.Combine(userDirectory, "profiles.json"),
                Path.Combine(userDirectory, "battery-preferences.json"),
                markerPath,
                validator,
                postCommitBoundary: null,
                transactionBoundary: null,
                writerScope);
            var overrides = new PersistentOverrideStore(
                Path.Combine(userDirectory, "manual-overrides.json"),
                beforeOverrideWrite,
                writerScope);
            var thermalConvergence = new ThermalConvergenceStateStore(
                Path.Combine(userDirectory, "thermal-convergence.json"),
                userSid,
                writerScope);

            await MigrateLegacyOwnerAsync(
                userSid,
                store,
                overrides,
                cancellationToken).ConfigureAwait(false);

            UserConfigurationContext context = await ReadContextAsync(
                store, overrides, thermalConvergence, writerScope, cancellationToken)
                .ConfigureAwait(false);
            ownedScope = null; // The returned context owns a newly acquired scope.
            return context;
        }
        finally
        {
            try
            {
                if (ownedScope is not null)
                {
                    await ownedScope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                migrationGate.Release();
            }
        }
    }

    private static async Task<UserConfigurationContext> ReadContextAsync(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrides,
        ThermalConvergenceStateStore thermalConvergence,
        UserConfigurationWriterScope? writerScope,
        CancellationToken cancellationToken)
    {
        BatteryConfiguration configuration = await store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        PersistentOverrideLoadResult loadedOverrides = await overrides.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        ThermalConvergenceLoadResult loadedThermalConvergence =
            await thermalConvergence.LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new UserConfigurationContext(
            store, overrides, configuration, loadedOverrides.Overrides,
            loadedOverrides.Disposition, loadedOverrides.QuarantineReason)
        {
            ThermalConvergenceStore = thermalConvergence,
            ThermalConvergence = loadedThermalConvergence,
            WriterScope = writerScope,
        };
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
        BatteryConfigurationInspection destinationConfiguration = await destination
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);
        BatteryConfigurationInspection legacy;
        try
        {
            legacy = await source.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigurationQuarantinedException exception) when (
            destinationConfiguration.Configuration.IsAuthoritative && !destinationConfiguration.WasEmpty)
        {
            // Only independently verified, existing user intent can bypass an
            // unresolved legacy source. Do not import it, mark migration complete,
            // or materialize new default intent for an empty user destination.
            DiagnosticTrace.TraceException(nameof(UserConfigurationRepository), nameof(MigrateLegacyOwnerAsync),
                exception, "Unresolved legacy configuration was preserved; the existing authoritative user aggregate remains independent.");
            return;
        }

        var sourceOverrides = new PersistentOverrideStore(
            Path.Combine(machineDataDirectory, "manual-overrides.json"));
        PersistentOverrideInspection legacyOverrides = await sourceOverrides
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

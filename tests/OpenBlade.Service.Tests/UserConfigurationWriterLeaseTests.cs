using System.ComponentModel;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.WriterFence.Scratch;

namespace OpenBlade.Service.Tests;

public sealed class UserConfigurationWriterLeaseTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"openblade-writer-fence-{Guid.NewGuid():N}");
    private readonly ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();
    private readonly SecurityIdentifier user = new("S-1-5-21-1-2-3-1001");

    [Fact]
    public async Task LoadAsync_HoldsNativeLeaseAcrossLegacyMigrationAndReturnedStoreWrites()
    {
        var writer = new ScratchConfigurationWriter(directory, user);
        var legacy = new BatteryConfigurationStore(
            Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "battery-preferences.json"),
            Path.Combine(directory, "battery-configuration.commit.json"),
            validator);
        ProfileSet legacyProfiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        await legacy.SaveConfigurationAsync(
            legacyProfiles, new BatteryAutomationPreferences(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(directory, "authorized-user.sid"), user.Value);
        var repository = new UserConfigurationRepository(directory, validator, writer.Acquire);

        using (writer.Acquire())
        {
            await AssertContendedAsync(() => repository.LoadAsync(user, CancellationToken.None));
            Assert.Equal([WriterLease.Name], Directory.GetFiles(writer.Root).Select(Path.GetFileName));
        }

        await using UserConfigurationContext context = await repository.LoadAsync(user, CancellationToken.None);
        Assert.Equal(75, context.Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.True(File.Exists(Path.Combine(writer.Root, ".legacy-migration-complete")));
        await AssertContendedAsync(() => repository.LoadAsync(user, CancellationToken.None));

        ProfileSet updated = legacyProfiles with { BatteryPolicy = BatteryPolicy.Create(80) };
        await context.Store.SaveProfilesAsync(updated, CancellationToken.None);
        var overrides = PersistentOverrideState.Empty with { Performance = PerformancePreset.Silent };
        await context.OverrideStore.SaveAsync(overrides, CancellationToken.None);
        ThermalConvergenceStateStore thermal = Assert.IsType<ThermalConvergenceStateStore>(
            context.ThermalConvergenceStore);
        ThermalConvergenceLedgerState state = CreateLedger(context.Configuration);
        Assert.True((await thermal.TrySaveAsync(null, state, CancellationToken.None)).Applied);
        await context.DisposeAsync();

        await using UserConfigurationContext successor = await repository.LoadAsync(user, CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            context.Store.SaveConfigurationAsync(legacyProfiles, context.Configuration.Preferences, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            context.OverrideStore.SaveAsync(PersistentOverrideState.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            thermal.TrySaveAsync(null, state, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.OverrideStore.LoadAsync(CancellationToken.None));
        Assert.Equal(80, successor.Configuration.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(PerformancePreset.Silent, successor.Overrides.Performance);
        Assert.NotNull(successor.ThermalConvergence?.Sha256);
    }

    [Fact]
    public async Task LoadAsync_RefusesRecoveryBeforeChangingBytesAndReleasesFailedOrCanceledLease()
    {
        var writer = new ScratchConfigurationWriter(directory, user);
        await File.WriteAllTextAsync(Path.Combine(directory, "authorized-user.sid"), user.Value);
        string recovery = Path.Combine(writer.Root, "battery-configuration.commit.json.recovery-required");
        await File.WriteAllTextAsync(recovery, "preserve the opaque transaction record");
        var repository = new UserConfigurationRepository(directory, validator, writer.Acquire);

        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            repository.LoadAsync(user, CancellationToken.None));
        using (writer.Acquire())
        {
            Assert.Equal("preserve the opaque transaction record", await File.ReadAllTextAsync(recovery));
            Assert.Equal(2, Directory.GetFiles(writer.Root).Length);
            File.Delete(recovery);
        }

        using var cancellation = new CancellationTokenSource();
        var canceledRepository = new UserConfigurationRepository(directory, validator, (root, token) =>
        {
            IDisposable lease = writer.Acquire(root, token);
            cancellation.Cancel();
            return lease;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            canceledRepository.LoadAsync(user, cancellation.Token));
        Assert.Equal([WriterLease.Name], Directory.GetFiles(writer.Root).Select(Path.GetFileName));

        await using UserConfigurationContext context = await repository.LoadAsync(user, CancellationToken.None);
        await File.WriteAllTextAsync(recovery, "unresolved after activation");
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            context.Store.SaveProfilesAsync(context.Configuration.Profiles, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            context.OverrideStore.SaveAsync(PersistentOverrideState.Empty, CancellationToken.None));
        ThermalConvergenceStateStore thermal = context.ThermalConvergenceStore!;
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            thermal.TrySaveAsync(null, CreateLedger(context.Configuration), CancellationToken.None));
        Assert.False(File.Exists(thermal.Path));
        Assert.False(File.Exists(Path.Combine(writer.Root, "manual-overrides.json")));
        Assert.Equal("unresolved after activation", await File.ReadAllTextAsync(recovery));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_DrainsPendingWriteBeforeReleasingNativeLease(bool cancelWrite)
    {
        var writer = new ScratchConfigurationWriter(directory, user);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new UserConfigurationRepository(directory, validator, writer.Acquire, async token =>
        {
            writing.TrySetResult();
            await resume.Task.WaitAsync(token);
        });
        using var cancellation = new CancellationTokenSource();
        await using UserConfigurationContext context = await repository.LoadAsync(user, CancellationToken.None);
        var requested = PersistentOverrideState.Empty with { Performance = PerformancePreset.Silent };
        Task write = context.OverrideStore.SaveAsync(requested, cancellation.Token);
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task queued = context.Store.SaveProfilesAsync(context.Configuration.Profiles, CancellationToken.None);
            Task retirement = context.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            await AssertContendedAsync(() => repository.LoadAsync(user, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                context.OverrideStore.SaveAsync(requested, CancellationToken.None));

            if (cancelWrite)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            }
            else
            {
                resume.TrySetResult();
                await write;
            }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            await retirement;
        }
        finally
        {
            resume.TrySetResult();
        }

        await using UserConfigurationContext successor = await repository.LoadAsync(user, CancellationToken.None);
        Assert.Equal(cancelWrite ? null : PerformancePreset.Silent, successor.Overrides.Performance);
    }

    private ThermalConvergenceLedgerState CreateLedger(BatteryConfiguration configuration)
    {
        var automation = new ProfileAutomationState(
            configuration.Profiles, configuration.Preferences, PowerSource.PluggedIn);
        var observed = new ConfirmedDeviceState(
            PerformancePreset.Balanced, FanMode.Automatic, Battery: null, Lighting: null);
        return ThermalConvergenceLedgerState.Empty(user.Value) with
        {
            DeviceAuthorityEpoch = Guid.NewGuid(),
            DeviceFingerprint = "scratch",
            IntentGeneration = 1,
            EffectiveIntent = automation.Resolve(observed),
            PerformanceDesiredRevision = Guid.NewGuid().ToString("D"),
            FanDesiredRevision = Guid.NewGuid().ToString("D"),
            PolicyRevision = Guid.NewGuid().ToString("D"),
            SettledDestination = PowerSource.PluggedIn,
        };
    }

    internal static async Task AssertContendedAsync(Func<Task> action)
    {
        IOException error = await Assert.ThrowsAsync<IOException>(action);
        Assert.Equal(32, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

internal sealed class ScratchConfigurationWriter
{
    private readonly string rootId;
    private readonly string lockId;

    internal ScratchConfigurationWriter(string machineRoot, SecurityIdentifier sid)
        : this(Path.Combine(machineRoot, "users", sid.Value))
    {
    }

    internal ScratchConfigurationWriter(string rootPath)
    {
        Root = rootPath;
        Directory.CreateDirectory(Root);
        using var root = PinnedRoot.OpenPath(Root);
        rootId = root.Info.Identity;
        using var file = Native.OpenRelative(root.Handle, WriterLease.Name, directory: false, create: true);
        lockId = Native.Inspect(file, directory: false).Identity;
    }

    internal string Root { get; }

    internal IDisposable Acquire() => WriterLease.AcquireExactRoot(Root, rootId, lockId);

    internal IDisposable Acquire(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(Root, path);
        return Acquire();
    }
}

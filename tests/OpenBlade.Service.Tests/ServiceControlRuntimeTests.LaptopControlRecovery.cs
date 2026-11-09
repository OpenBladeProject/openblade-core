using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [MemberData(nameof(WindowsReplayData.Cases), "laptopRecovery", MemberType = typeof(WindowsReplayData))]
    public async Task WindowsReplay_LaptopRecoverySettlesOnlyWithEvidence(string id)
    {
        using JsonDocument fixture = WindowsReplayData.Open();
        JsonElement example = WindowsReplayData.Find(fixture, "laptopRecovery", id);
        JsonElement input = example.GetProperty("input");
        JsonElement expected = example.GetProperty("expected");
        const string identity = "synthetic-device";
        var store = new LaptopControlPendingStore(Path.Combine(directory, "replay-pending.json"));
        ApplyOneShotLaptopControl request = input.GetProperty("requested").GetString() == "Microphone"
            ? new(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: true)
            : FunctionKeysRequest;
        FunctionKeyPrimaryMode? readback = input.GetProperty("readback").GetString() is { } mode
            ? Enum.Parse<FunctionKeyPrimaryMode>(mode)
            : null;
        var device = new CountingBladeDevice { FunctionKeyPrimary = readback };
        string? action = input.GetProperty("action").GetString();
        if (action == "Restart")
        {
            // A different machine name is provenance only.
            Assert.True(await store.TrySaveAsync(null, new(1, "prior-name", identity,
                [new(request, true)]), CancellationToken.None));
            var restarted = new LaptopControlExecutor(store);
            await restarted.ReconcileAsync(device, input.GetProperty("identity").GetString(), CancellationToken.None);
        }
        else
        {
            var executor = new LaptopControlExecutor(store);
            using var cancellation = new CancellationTokenSource();
            int authorityChecks = 0;
            bool WriteAllowed()
            {
                if (++authorityChecks == 2 && action == "CanceledBefore")
                {
                    Assert.True(store.Load().RequiresReconciliation);
                    cancellation.Cancel();
                    return false;
                }
                return true;
            }

            device.AfterOneShotSet = _ =>
            {
                Assert.True(store.Load().RequiresReconciliation);
                device.FunctionKeyPrimary = readback;
                if (action == "CanceledDuring")
                {
                    cancellation.Cancel();
                }
            };
            device.OneShotFailure = action switch
            {
                "CanceledDuring" => new OperationCanceledException(cancellation.Token),
                "Indeterminate" => new BladeWriteException(WriteSubmissionState.Indeterminate, "Possible submission."),
                _ => null,
            };
            Task<LaptopControlApplyOutcome> apply = executor.ExecuteAsync(
                device, identity, request, true, WriteAllowed, cancellation.Token);
            if (action is "CanceledBefore" or "CanceledDuring")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            }
            else
            {
                await apply;
            }
        }

        LaptopControlPendingLoad load = store.Load();
        Assert.Null(load.Failure);
        switch (expected.GetProperty("journal").GetString())
        {
            case "Removed":
                Assert.Empty(load.Document!.Entries);
                break;
            case "Pending":
                Assert.Equal(new LaptopControlPendingEntry(request, true), Assert.Single(load.Document!.Entries));
                break;
            case "Tombstone":
                Assert.Equal(new LaptopControlPendingEntry(request, false), Assert.Single(load.Document!.Entries));
                break;
            default:
                throw new InvalidDataException("Unknown journal expectation.");
        }
        Assert.Equal(expected.GetProperty("setters").GetInt32(), device.OneShotSetCalls);
    }


    [Fact]
    public async Task LaptopRecovery_PendingBlocksOwnedReactiveInput()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.Reactive;
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(LightingRendererOwnershipState.Reactive, runtime.GetStatus().LightingRendererOwnership);
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;
        await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, CancellationToken.None);

        ControlOperationResult result = await runtime.PulseReactiveKeyAsync(new ReactiveKeyPulse(53), CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Device.ReactivePulseCalls);
    }

    [Fact]
    public async Task LaptopRecovery_RetainedAdmissionSeesWriteAheadAndReadOnlyRecoveryImmediately()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        Func<bool> admission = Assert.IsType<Func<bool>>(context.Provider.RecoveryWriteAdmission);
        Assert.True(admission());
        context.Device.AfterOneShotSet = _ =>
        {
            Assert.False(admission());
            context.Device.FunctionKeyPrimary = null;
        };
        await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, CancellationToken.None);
        Assert.False(admission());
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys;
        context.Device.BeforeFunctionKeyRead = () => Assert.False(admission());

        await runtime.ObserveAsync(CancellationToken.None);

        Assert.True(admission());
        Assert.Equal(1, context.Device.OneShotSetCalls);
    }

    private const string LaptopIdentity = "RZ09-0581|1532:02E0";
    private static readonly ApplyOneShotLaptopControl FunctionKeysRequest = new(
        OneShotLaptopControl.FunctionKeyPrimary, FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys);

    [Fact]
    public async Task LaptopRecovery_PendingGuardsMutationsButKeepsReadOnlyObservation()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;
        await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, CancellationToken.None);
        int setters = context.Device.TotalSetterCalls;
        int stateReads = context.Device.StateReadCalls;

        await runtime.MaintainDriverModeAsync(CancellationToken.None);
        await runtime.SetBatteryProtectionAsync(false, CancellationToken.None);
        await runtime.SetLightingOverrideAsync(new(runtime.GetStatus().DesiredState.Lighting.Value,
            OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);
        await runtime.PrepareForShutdownAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Equal(0, context.Device.DriverModeEnsureCalls);
        Assert.Equal(0, context.Device.DriverModeReleaseCalls);
        Assert.True(context.Device.StateReadCalls > stateReads);
        Assert.True(runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaptopRecovery_ResolvingObservationDoesNotRestoreFaultedLighting(bool matching)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.FunctionKeyPrimary },
        };
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        await runtime.ObserveAsync(CancellationToken.None);
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;
        await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, CancellationToken.None);
        context.Device.AfterOneShotSet = null;
        context.Device.FunctionKeyPrimary = matching ? FunctionKeyPrimaryMode.FunctionKeys : FunctionKeyPrimaryMode.MultimediaKeys;
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.Faulted;
        int writesBefore = context.Device.TotalSetterCalls;

        await runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(writesBefore, context.Device.TotalSetterCalls);
        Assert.False(runtime.GetStatus().WritesBlocked);
        Assert.Equal(context.Device.FunctionKeyPrimary, runtime.GetStatus().FunctionKeyPrimary);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(writesBefore + 1, context.Device.TotalSetterCalls);
        Assert.Equal(LightingRendererOwnershipState.Static, runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task LaptopRecovery_RestartRequiresReadAndDifferingStateDoesNotReplay()
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "executor-pending.json"));
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys };
        device.AfterOneShotSet = _ =>
        {
            Assert.True(store.Load().RequiresReconciliation);
            device.FunctionKeyPrimary = null;
        };
        var executor = new LaptopControlExecutor(store);
        LaptopControlApplyOutcome first = await executor.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, false, static () => true, CancellationToken.None);
        Assert.True(first.StopFurtherWrites);

        var restarted = new LaptopControlExecutor(store);
        Assert.True(restarted.RequiresReconciliation);
        await restarted.ExecuteAsync(device, LaptopIdentity,
            new(OneShotLaptopControl.GamingMode, Enabled: true), true, static () => true, CancellationToken.None);
        Assert.Equal(1, device.OneShotSetCalls);

        device.AfterOneShotSet = null;
        device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        await restarted.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);
        Assert.False(restarted.RequiresReconciliation);
        LaptopControlApplyOutcome automatic = await restarted.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, false, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, automatic.Result.Status);
        Assert.False(automatic.StopFurtherWrites);
        Assert.Equal(1, device.OneShotSetCalls);

        // Repeated context reports cannot override the tombstone, including after restart.
        restarted = new LaptopControlExecutor(store);
        await restarted.ExecuteAsync(device, LaptopIdentity, FunctionKeysRequest, false, static () => true, CancellationToken.None);
        Assert.Equal(1, device.OneShotSetCalls);
        LaptopControlApplyOutcome explicitRetry = await restarted.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, true, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, explicitRetry.Result.Status);
        Assert.Equal(2, device.OneShotSetCalls);
        Assert.Empty(store.Load().Document!.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RZ09-0528|1532:02C6")]
    public async Task LaptopRecovery_WrongOrUnknownIdentityCannotClearPending(string? identity)
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "identity-pending.json"));
        await store.TrySaveAsync(null, new(1, Environment.MachineName, LaptopIdentity,
            [new(FunctionKeysRequest, true)]), CancellationToken.None);
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys };
        var executor = new LaptopControlExecutor(store);
        await executor.ReconcileAsync(device, identity, CancellationToken.None);
        Assert.True(executor.RequiresReconciliation);
        Assert.True(store.Load().RequiresReconciliation);
        Assert.Equal(0, device.OneShotSetCalls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task LaptopRecovery_CorruptStateFailsClosed(string contents)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "invalid-pending.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new LaptopControlPendingStore(path);
        Assert.NotNull(store.Load().Failure);
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys };
        var executor = new LaptopControlExecutor(store);
        LaptopControlApplyOutcome result = await executor.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, true, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Result.Status);
        Assert.Equal(0, device.OneShotSetCalls);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LaptopRecovery_UnreadableStateIsNotAbsent()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "locked-pending.json");
        await File.WriteAllTextAsync(path, "{}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new LaptopControlPendingStore(path);
        Assert.NotNull(store.Load().Failure);
        Assert.True(new LaptopControlExecutor(store).RequiresReconciliation);
    }

    [Fact]
    public async Task LaptopRecovery_FailedIntentPersistencePreventsSetter()
    {
        Directory.CreateDirectory(directory);
        string parentFile = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(parentFile, "blocked");
        var executor = new LaptopControlExecutor(new LaptopControlPendingStore(Path.Combine(parentFile, "pending.json")));
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys };
        LaptopControlApplyOutcome result = await executor.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, true, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Result.Status);
        Assert.Equal(0, device.OneShotSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaptopRecovery_WriteOnlyMicrophoneJournalsBeforeSubmission(bool uncertain)
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "microphone-pending.json"));
        var device = new CountingBladeDevice();
        device.AfterOneShotSet = _ => Assert.True(store.Load().RequiresReconciliation);
        if (uncertain)
        {
            device.OneShotFailure = new BladeWriteException(WriteSubmissionState.Indeterminate, "Unknown completion");
        }
        var executor = new LaptopControlExecutor(store);
        LaptopControlApplyOutcome result = await executor.ExecuteAsync(device, LaptopIdentity,
            new(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: true), true, static () => true, CancellationToken.None);
        Assert.Equal(uncertain ? ControlOperationStatus.Failed : ControlOperationStatus.Applied, result.Result.Status);
        Assert.Equal(uncertain, executor.RequiresReconciliation);
        var restarted = new LaptopControlExecutor(store);
        await restarted.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);
        Assert.Equal(uncertain, restarted.RequiresReconciliation);
        if (uncertain)
        {
            Assert.Contains("cannot be read", restarted.PendingMessage, StringComparison.Ordinal);
        }
        Assert.Equal(1, device.OneShotSetCalls);
    }

    [Fact]
    public async Task LaptopRecovery_KnownNonSubmissionClearsIntent()
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "refused-pending.json"));
        var device = new CountingBladeDevice
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            OneShotFailure = new BladeWriteException(WriteSubmissionState.KnownNotApplied, "Refused before submission"),
        };
        var executor = new LaptopControlExecutor(store);
        LaptopControlApplyOutcome result = await executor.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, true, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Result.Status);
        Assert.False(executor.RequiresReconciliation);
        Assert.Empty(store.Load().Document!.Entries);
    }

    [Fact]
    public async Task LaptopRecovery_CanceledSubmissionRetainsIntent()
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "canceled-pending.json"));
        using var cancellation = new CancellationTokenSource();
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys };
        device.AfterOneShotSet = _ => cancellation.Cancel();
        device.OneShotFailure = new OperationCanceledException(cancellation.Token);
        var executor = new LaptopControlExecutor(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(device, LaptopIdentity,
            FunctionKeysRequest, true, static () => true, cancellation.Token));
        Assert.True(new LaptopControlExecutor(store).RequiresReconciliation);
        Assert.Equal(1, device.OneShotSetCalls);
    }
}

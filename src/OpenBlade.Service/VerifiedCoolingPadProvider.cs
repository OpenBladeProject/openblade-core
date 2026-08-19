using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public enum CoolingPadObservationFailure
{
    None,
    Discovery,
    IdentityOrAccess,
    Conflict,
    Transport,
    Protocol,
    LowPower,
    Canceled,
    Disposed,
}

public readonly record struct CoolingPadObservationSnapshot(
    CoolingPadConnectionState Connection,
    byte? OpaqueFanObservation,
    bool IsFresh,
    CoolingPadObservationFailure Failure);

public interface IVerifiedCoolingPadProvider : IAsyncDisposable
{
    DeviceFeatureAccess FanObservationAccess { get; }

    Task<CoolingPadObservationSnapshot> ObserveAsync(
        CancellationToken cancellationToken);

    Task<CoolingPadObservationSnapshot> ObserveFreshAsync(
        CancellationToken cancellationToken);

    Task InvalidateAsync(CancellationToken cancellationToken);
}

internal interface ICoolingPadHostControlExecutorAdapter
{
    Task AcquireAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}

internal sealed class ProtocolCoolingPadHostControlExecutorAdapter(
    CoolingPadHostControlExecutor executor) : ICoolingPadHostControlExecutorAdapter
{
    private readonly CoolingPadHostControlExecutor executor = executor
        ?? throw new ArgumentNullException(nameof(executor));

    public Task AcquireAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken) =>
        executor.AcquireAsync(target, cancellationToken);

    public Task UpdateAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken) =>
        executor.UpdateAsync(target, cancellationToken);

    public Task ReleaseAsync(CancellationToken cancellationToken) =>
        executor.ReleaseAsync(cancellationToken);
}

internal enum CoolingPadLightingWriteOutcome
{
    Success,
    KnownNotApplied,
    Indeterminate,
}

internal interface ICoolingPadLightingSessionOwner
{
    Task<CoolingPadLightingBrightnessObservation> QueryBrightnessAsync(
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SubmitFrameAsync(
        IReadOnlyList<RgbColor> slots,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SetBrightnessRawForLifecycleAsync(
        byte raw,
        CancellationToken cancellationToken);
}

internal interface ICoolingPadLightingExecutorAdapter
{
    Task<CoolingPadLightingBrightnessObservation> QueryBrightnessAsync(
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SubmitFrameAsync(
        IReadOnlyList<RgbColor> slots,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingWriteOutcome> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken);
}

internal sealed class ProtocolCoolingPadLightingExecutorAdapter(
    ICoolingPadLightingExecutor executor) : ICoolingPadLightingExecutorAdapter
{
    private readonly ICoolingPadLightingExecutor executor = executor
        ?? throw new ArgumentNullException(nameof(executor));

    public async Task<CoolingPadLightingBrightnessObservation> QueryBrightnessAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<CoolingPadBrightnessReading> result =
            await executor.QueryBrightnessAsync(cancellationToken)
                .ConfigureAwait(false);
        return result.Failure is null && result.Value is { } reading
            ? new CoolingPadLightingBrightnessObservation(
                reading.Raw,
                CoolingPadLightingWriteOutcome.Success)
            : new CoolingPadLightingBrightnessObservation(
                Raw: null,
                CoolingPadLightingWriteOutcome.KnownNotApplied);
    }

    public async Task<CoolingPadLightingWriteOutcome> SubmitFrameAsync(
        IReadOnlyList<RgbColor> slots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
                new CoolingPadLightingFrame(slots.ToArray()),
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result.Failure);
    }

    public async Task<CoolingPadLightingWriteOutcome> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken)
    {
        CoolingPadLightingSinkResult result = await executor.SetBrightnessAsync(
                percent,
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result.Failure);
    }

    public async Task<CoolingPadLightingWriteOutcome> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken)
    {
        CoolingPadLightingSinkResult result = await executor.SetBrightnessRawAsync(
                raw,
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result.Failure);
    }

    private static CoolingPadLightingWriteOutcome Map(
        CoolingPadLightingSinkFailure failure) => failure switch
        {
            CoolingPadLightingSinkFailure.None =>
                CoolingPadLightingWriteOutcome.Success,
            CoolingPadLightingSinkFailure.KnownNotApplied =>
                CoolingPadLightingWriteOutcome.KnownNotApplied,
            CoolingPadLightingSinkFailure.Indeterminate =>
                CoolingPadLightingWriteOutcome.Indeterminate,
            _ => CoolingPadLightingWriteOutcome.Indeterminate,
        };
}

internal sealed class ProviderCoolingPadLightingSessionOwner(
    WindowsVerifiedCoolingPadProvider provider) : ICoolingPadLightingSessionOwner
{
    private readonly WindowsVerifiedCoolingPadProvider provider = provider
        ?? throw new ArgumentNullException(nameof(provider));

    public Task<CoolingPadLightingBrightnessObservation> QueryBrightnessAsync(
        CancellationToken cancellationToken) =>
        provider.QueryLightingBrightnessAsync(cancellationToken);

    public Task<CoolingPadLightingWriteOutcome> SubmitFrameAsync(
        IReadOnlyList<RgbColor> slots,
        CancellationToken cancellationToken) => provider.SubmitLightingFrameAsync(
        slots,
        cancellationToken);

    public Task<CoolingPadLightingWriteOutcome> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken) => provider.SetLightingBrightnessAsync(
        percent,
        cancellationToken);

    public Task<CoolingPadLightingWriteOutcome> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken) => provider.SetLightingBrightnessRawAsync(
        raw,
        cancellationToken);

    public Task<CoolingPadLightingWriteOutcome> SetBrightnessRawForLifecycleAsync(
        byte raw,
        CancellationToken cancellationToken) =>
        provider.SetLightingBrightnessRawForLifecycleAsync(raw, cancellationToken);
}

internal sealed class ProviderCoolingPadLightingSink(
    ICoolingPadLightingSessionOwner owner) : ICoolingPadLightingSink
{
    private readonly ICoolingPadLightingSessionOwner owner = owner
        ?? throw new ArgumentNullException(nameof(owner));

    public async Task<HidSessionResult<CoolingPadBrightnessReading>>
        QueryBrightnessAsync(CancellationToken cancellationToken)
    {
        CoolingPadLightingBrightnessObservation result =
            await owner.QueryBrightnessAsync(cancellationToken)
                .ConfigureAwait(false);
        return result.Outcome == CoolingPadLightingWriteOutcome.Success
            && result.Raw is { } raw
            ? new HidSessionResult<CoolingPadBrightnessReading>(
                new CoolingPadBrightnessReading(raw),
                Failure: null)
            : new HidSessionResult<CoolingPadBrightnessReading>(
                default,
                result.Outcome == CoolingPadLightingWriteOutcome.Indeterminate
                    ? HidSessionFailureKind.Poisoned
                    : HidSessionFailureKind.ProtocolFailure);
    }

    public async Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
        CoolingPadLightingFrame frame,
        CancellationToken cancellationToken) => Map(
        await owner.SubmitFrameAsync(frame.ToArray(), cancellationToken)
            .ConfigureAwait(false));

    public async Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken) => Map(
        await owner.SetBrightnessAsync(percent, cancellationToken)
            .ConfigureAwait(false));

    public async Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken) => Map(
        await owner.SetBrightnessRawAsync(raw, cancellationToken)
            .ConfigureAwait(false));

    public async Task<CoolingPadLightingSinkResult>
        SetBrightnessRawForLifecycleAsync(
            byte raw,
            CancellationToken cancellationToken) => Map(
        await owner.SetBrightnessRawForLifecycleAsync(raw, cancellationToken)
            .ConfigureAwait(false));

    private static CoolingPadLightingSinkResult Map(
        CoolingPadLightingWriteOutcome outcome) => new(outcome switch
        {
            CoolingPadLightingWriteOutcome.Success =>
                CoolingPadLightingSinkFailure.None,
            CoolingPadLightingWriteOutcome.KnownNotApplied =>
                CoolingPadLightingSinkFailure.KnownNotApplied,
            CoolingPadLightingWriteOutcome.Indeterminate =>
                CoolingPadLightingSinkFailure.Indeterminate,
            _ => CoolingPadLightingSinkFailure.Indeterminate,
        });
}

internal readonly record struct CoolingPadLightingBrightnessObservation(
    byte? Raw,
    CoolingPadLightingWriteOutcome Outcome);

internal sealed class DiscoveryOnlyCoolingPadProvider(
    ICoolingPadDiscoverySource discovery) : IVerifiedCoolingPadProvider
{
    private readonly ICoolingPadDiscoverySource discovery = discovery
        ?? throw new ArgumentNullException(nameof(discovery));

    public DeviceFeatureAccess FanObservationAccess => DeviceFeatureAccess.ObservedOnly;

    public Task<CoolingPadObservationSnapshot> ObserveAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new CoolingPadObservationSnapshot(
                CoolingPadConnectionState.ObservationFailed,
                null,
                IsFresh: false,
                CoolingPadObservationFailure.Canceled));
        }

        CoolingPadConnectionState connection = discovery.Observe();
        return Task.FromResult(new CoolingPadObservationSnapshot(
            connection,
            null,
            IsFresh: connection != CoolingPadConnectionState.ObservationFailed,
            connection == CoolingPadConnectionState.ObservationFailed
                ? CoolingPadObservationFailure.Discovery
                : CoolingPadObservationFailure.None));
    }

    public Task<CoolingPadObservationSnapshot> ObserveFreshAsync(
        CancellationToken cancellationToken) => ObserveAsync(cancellationToken);

    public Task InvalidateAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class WindowsVerifiedCoolingPadProvider
    : IVerifiedCoolingPadProvider,
      ICoolingPadFanSemanticBackend
{
    internal static readonly TimeSpan DefaultObservationInterval =
        TimeSpan.FromSeconds(15);

    private readonly IProductionCoolingPadPortFactory factory;
    private readonly ICoolingPadDiscoverySource discovery;
    private readonly bool readQueryAdmitted;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan observationInterval;
    private readonly LowPowerGate? lowPowerGate;
    private readonly Func<IProductionCoolingPadPort,
        ICoolingPadHostControlExecutorAdapter?> createFanExecutor;
    private readonly Func<IProductionCoolingPadPort,
        ICoolingPadLightingExecutorAdapter?>
        createLightingExecutor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IProductionCoolingPadPort? port;
    private byte? lastOpaqueFanObservation;
    private long lastSuccessfulObservationTimestamp;
    private int disposed;

    public WindowsVerifiedCoolingPadProvider()
        : this(
            ProductionCoolingPadFactory.CreateWindows(),
            CoolingPadDiscoveryFactory.CreateWindows(),
            WindowsHostIdentitySource.IsRz090581WindowsHost(
                allowUntestedFirmware: true),
            TimeProvider.System,
            DefaultObservationInterval,
            lowPowerGate: null)
    {
    }

    internal WindowsVerifiedCoolingPadProvider(
        IProductionCoolingPadPortFactory factory,
        ICoolingPadDiscoverySource discovery,
        bool readQueryAdmitted = true,
        TimeProvider? timeProvider = null,
        TimeSpan? observationInterval = null,
        LowPowerGate? lowPowerGate = null,
        Func<IProductionCoolingPadPort,
            ICoolingPadHostControlExecutorAdapter?>? createFanExecutor = null,
        Func<IProductionCoolingPadPort,
            ICoolingPadLightingExecutorAdapter?>? createLightingExecutor = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.readQueryAdmitted = readQueryAdmitted;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.observationInterval = observationInterval ?? TimeSpan.Zero;
        this.lowPowerGate = lowPowerGate;
        this.createFanExecutor = createFanExecutor ?? CreateProtocolFanExecutor;
        this.createLightingExecutor = createLightingExecutor
            ?? CreateProtocolLightingExecutor;
        if (this.observationInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(observationInterval));
        }
    }

    public DeviceFeatureAccess FanObservationAccess => readQueryAdmitted
        ? DeviceFeatureAccess.ReadOnly
        : DeviceFeatureAccess.ObservedOnly;

    public async Task<CoolingPadObservationSnapshot> ObserveAsync(
        CancellationToken cancellationToken) => await ObserveCoreAsync(
            forceRefresh: false,
            cancellationToken).ConfigureAwait(false);

    public async Task<CoolingPadObservationSnapshot> ObserveFreshAsync(
        CancellationToken cancellationToken) => await ObserveCoreAsync(
            forceRefresh: true,
        cancellationToken).ConfigureAwait(false);

    Task ICoolingPadFanSemanticBackend.AcquireTargetAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken) => ExecuteFanWriteAsync(
        target,
        CoolingPadFanWriteKind.Acquire,
        cancellationToken);

    Task ICoolingPadFanSemanticBackend.UpdateTargetAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken) => ExecuteFanWriteAsync(
        target,
        CoolingPadFanWriteKind.Update,
        cancellationToken);

    Task ICoolingPadFanSemanticBackend.HandbackAsync(
        CancellationToken cancellationToken) => ExecuteFanWriteAsync(
        target: null,
        CoolingPadFanWriteKind.Release,
        cancellationToken);

    Task ICoolingPadFanSemanticBackend.HandbackForLifecycleAsync(
        CancellationToken cancellationToken) =>
        ExecuteOwnedFanLifecycleHandbackAsync(cancellationToken);

    private async Task ExecuteOwnedFanLifecycleHandbackAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || port is not { } activePort
                || createFanExecutor(activePort) is not { } executor)
            {
                throw WriteUnavailable(
                    ownedOperation: true,
                    "The already-open cooling-pad session is unavailable for lifecycle handback.");
            }

            try
            {
                await executor.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    && exception.FailureKind == HidSessionFailureKind.Exhausted)
            {
                await DropPortAsync().ConfigureAwait(false);
                throw;
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    && IsControllerAuthorityFailure(exception.FailureKind))
            {
                await DropPortAsync().ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Cooling-pad controller authority changed while OpenBlade-owned fan control required lifecycle handback.",
                    exception);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                await DropPortAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteFanWriteAsync(
        CoolingPadFanTarget? target,
        CoolingPadFanWriteKind kind,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool ownedOperation = kind != CoolingPadFanWriteKind.Acquire;
            if (Volatile.Read(ref disposed) != 0)
            {
                throw WriteUnavailable(
                    ownedOperation,
                    "The cooling-pad provider is disposed.");
            }
            if (!TryAcquireOperation(out LowPowerOperationPermit permit))
            {
                throw WriteUnavailable(
                    ownedOperation,
                    "Cooling-pad writes are blocked during low power.");
            }

            CoolingPadConnectionState connection = discovery.Observe();
            if (connection != CoolingPadConnectionState.PresentExact)
            {
                await DropPortAsync().ConfigureAwait(false);
                throw WriteUnavailable(
                    ownedOperation,
                    $"Exact cooling-pad identity is unavailable ({connection}).");
            }
            if (!IsOperationCurrent(permit))
            {
                await DropPortAsync().ConfigureAwait(false);
                throw WriteUnavailable(
                    ownedOperation,
                    "Low-power state changed before the cooling-pad write.");
            }

            if (port is null)
            {
                HidSessionResult<IProductionCoolingPadPort> opened =
                    await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Failure is not null || opened.Value is null)
                {
                    if (opened.Value is not null)
                    {
                        await DisposeIgnoringFailureAsync(opened.Value)
                            .ConfigureAwait(false);
                    }

                    throw WriteUnavailable(
                        ownedOperation,
                        $"The exact cooling-pad session could not be opened ({opened.Failure}).");
                }

                port = opened.Value;
            }

            if (!IsOperationCurrent(permit)
                || port is not { } activePort
                || createFanExecutor(activePort) is not { } executor)
            {
                await DropPortAsync().ConfigureAwait(false);
                throw WriteUnavailable(
                    ownedOperation,
                    "The production cooling-pad session is unavailable for a typed write.");
            }

            try
            {
                switch (kind)
                {
                    case CoolingPadFanWriteKind.Acquire:
                        await executor.AcquireAsync(target!.Value, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case CoolingPadFanWriteKind.Update:
                        await executor.UpdateAsync(target!.Value, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case CoolingPadFanWriteKind.Release:
                        await executor.ReleaseAsync(cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "The cooling-pad fan write kind is invalid.");
                }
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    && exception.FailureKind == HidSessionFailureKind.Exhausted)
            {
                // Exhaustion is proven before submission. Retire the spent
                // session, but do not replay the same semantic operation.
                await DropPortAsync().ConfigureAwait(false);
                throw;
            }
            catch (BladeWriteException exception)
                when (ownedOperation
                    && exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    && IsControllerAuthorityFailure(exception.FailureKind))
            {
                await DropPortAsync().ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Cooling-pad controller authority changed while OpenBlade still owned fan control.",
                    exception);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                await DropPortAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal ICoolingPadLightingSessionOwner CreateLightingSessionOwner() =>
        new ProviderCoolingPadLightingSessionOwner(this);

    internal Task<CoolingPadLightingBrightnessObservation>
        QueryLightingBrightnessAsync(CancellationToken cancellationToken) =>
        ExecuteLightingQueryAsync(cancellationToken);

    internal Task<CoolingPadLightingWriteOutcome> SubmitLightingFrameAsync(
        IReadOnlyList<RgbColor> slots,
        CancellationToken cancellationToken) => ExecuteLightingWriteAsync(
        (executor, token) => executor.SubmitFrameAsync(slots, token),
        cancellationToken);

    internal Task<CoolingPadLightingWriteOutcome> SetLightingBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken) => ExecuteLightingWriteAsync(
        (executor, token) => executor.SetBrightnessAsync(percent, token),
        cancellationToken);

    internal Task<CoolingPadLightingWriteOutcome> SetLightingBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken) => ExecuteLightingWriteAsync(
        (executor, token) => executor.SetBrightnessRawAsync(raw, token),
        cancellationToken);

    internal Task<CoolingPadLightingWriteOutcome>
        SetLightingBrightnessRawForLifecycleAsync(
            byte raw,
            CancellationToken cancellationToken) =>
        ExecuteOwnedLightingLifecycleCleanupAsync(raw, cancellationToken);

    private async Task<CoolingPadLightingWriteOutcome>
        ExecuteOwnedLightingLifecycleCleanupAsync(
            byte raw,
            CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || port is not { } activePort
                || createLightingExecutor(activePort) is not { } executor)
            {
                return LightingKnownNotApplied();
            }

            CoolingPadLightingWriteOutcome result =
                await executor.SetBrightnessRawAsync(raw, cancellationToken)
                    .ConfigureAwait(false);
            if (result == CoolingPadLightingWriteOutcome.Indeterminate)
            {
                await DropPortAsync().ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CoolingPadLightingWriteOutcome> ExecuteLightingWriteAsync(
        Func<ICoolingPadLightingExecutorAdapter, CancellationToken,
            Task<CoolingPadLightingWriteOutcome>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || !TryAcquireOperation(out LowPowerOperationPermit permit))
            {
                return LightingKnownNotApplied();
            }

            CoolingPadConnectionState connection = discovery.Observe();
            if (connection != CoolingPadConnectionState.PresentExact)
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingKnownNotApplied();
            }
            if (!IsOperationCurrent(permit))
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingKnownNotApplied();
            }

            if (port is null)
            {
                HidSessionResult<IProductionCoolingPadPort> opened =
                    await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Failure is not null || opened.Value is null)
                {
                    if (opened.Value is not null)
                    {
                        await DisposeIgnoringFailureAsync(opened.Value)
                            .ConfigureAwait(false);
                    }

                    return LightingKnownNotApplied();
                }

                port = opened.Value;
            }

            if (!IsOperationCurrent(permit)
                || port is not { } activePort
                || createLightingExecutor(activePort) is not { } executor)
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingKnownNotApplied();
            }

            CoolingPadLightingWriteOutcome result =
                await execute(executor, cancellationToken).ConfigureAwait(false);
            if (result == CoolingPadLightingWriteOutcome.Indeterminate)
            {
                await DropPortAsync().ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CoolingPadLightingBrightnessObservation>
        ExecuteLightingQueryAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || !TryAcquireOperation(out LowPowerOperationPermit permit))
            {
                return LightingQueryKnownNotApplied();
            }

            CoolingPadConnectionState connection = discovery.Observe();
            if (connection != CoolingPadConnectionState.PresentExact)
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingQueryKnownNotApplied();
            }
            if (!IsOperationCurrent(permit))
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingQueryKnownNotApplied();
            }

            if (port is null)
            {
                HidSessionResult<IProductionCoolingPadPort> opened =
                    await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Failure is not null || opened.Value is null)
                {
                    if (opened.Value is not null)
                    {
                        await DisposeIgnoringFailureAsync(opened.Value)
                            .ConfigureAwait(false);
                    }
                    return LightingQueryKnownNotApplied();
                }
                port = opened.Value;
            }

            if (!IsOperationCurrent(permit)
                || port is not { } activePort
                || createLightingExecutor(activePort) is not { } executor)
            {
                await DropPortAsync().ConfigureAwait(false);
                return LightingQueryKnownNotApplied();
            }

            CoolingPadLightingBrightnessObservation result =
                await executor.QueryBrightnessAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (result.Outcome != CoolingPadLightingWriteOutcome.Success)
            {
                await DropPortAsync().ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CoolingPadObservationSnapshot> ObserveCoreAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return Snapshot(
                CoolingPadConnectionState.Unavailable,
                fresh: false,
                CoolingPadObservationFailure.Disposed);
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Snapshot(
                CoolingPadConnectionState.ObservationFailed,
                fresh: false,
                CoolingPadObservationFailure.Canceled);
        }

        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return Snapshot(
                    CoolingPadConnectionState.Unavailable,
                    fresh: false,
                    CoolingPadObservationFailure.Disposed);
            }

            if (!TryAcquireOperation(out LowPowerOperationPermit permit))
            {
                return Snapshot(
                    CoolingPadConnectionState.ObservationFailed,
                    fresh: false,
                    CoolingPadObservationFailure.LowPower);
            }

            if (port is null)
            {
                if (!IsOperationCurrent(permit))
                {
                    return LowPowerSnapshot();
                }

                CoolingPadConnectionState connection = discovery.Observe();
                if (connection != CoolingPadConnectionState.PresentExact)
                {
                    if (connection is CoolingPadConnectionState.Absent
                        or CoolingPadConnectionState.Ambiguous)
                    {
                        lastOpaqueFanObservation = null;
                    }

                    return Snapshot(
                        connection,
                        fresh: connection != CoolingPadConnectionState.ObservationFailed,
                        connection == CoolingPadConnectionState.ObservationFailed
                            ? CoolingPadObservationFailure.Discovery
                            : CoolingPadObservationFailure.None);
                }

                if (!readQueryAdmitted)
                {
                    return Snapshot(
                        CoolingPadConnectionState.PresentExact,
                        fresh: true,
                        CoolingPadObservationFailure.None);
                }

                if (!IsOperationCurrent(permit))
                {
                    return LowPowerSnapshot();
                }

                HidSessionResult<IProductionCoolingPadPort> opened =
                    await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Failure is not null || opened.Value is null)
                {
                    if (opened.Value is not null)
                    {
                        await DisposeIgnoringFailureAsync(opened.Value).ConfigureAwait(false);
                    }

                    return Snapshot(
                        CoolingPadConnectionState.ObservationFailed,
                        fresh: false,
                        MapFailure(opened.Failure));
                }

                port = opened.Value;
                if (!IsOperationCurrent(permit))
                {
                    await DropPortAsync().ConfigureAwait(false);
                    return LowPowerSnapshot();
                }
            }
            else if (!forceRefresh
                && lastSuccessfulObservationTimestamp != 0
                && timeProvider.GetElapsedTime(lastSuccessfulObservationTimestamp)
                    < observationInterval)
            {
                // The value is still within the reviewed cache lifetime. It
                // remains fresh even though this call did not issue a new HID
                // query; callers that require a new query use ObserveFreshAsync.
                return Snapshot(
                    CoolingPadConnectionState.PresentExact,
                    fresh: true,
                    CoolingPadObservationFailure.None);
            }

            else if (port is not null)
            {
                if (!IsOperationCurrent(permit))
                {
                    await DropPortAsync().ConfigureAwait(false);
                    return LowPowerSnapshot();
                }

                // Revalidate exact single-device discovery on every actual refresh.
                // A retained read-only handle must not hide detach, ambiguity, or
                // discovery failure, and future writers will rely on this boundary.
                CoolingPadConnectionState refreshedConnection = discovery.Observe();
                if (refreshedConnection != CoolingPadConnectionState.PresentExact)
                {
                    await DropPortAsync().ConfigureAwait(false);
                    if (refreshedConnection is CoolingPadConnectionState.Absent
                        or CoolingPadConnectionState.Ambiguous)
                    {
                        lastOpaqueFanObservation = null;
                    }

                    return Snapshot(
                        refreshedConnection,
                        fresh: refreshedConnection
                            != CoolingPadConnectionState.ObservationFailed,
                        refreshedConnection
                            == CoolingPadConnectionState.ObservationFailed
                            ? CoolingPadObservationFailure.Discovery
                            : CoolingPadObservationFailure.None);
                }
            }

            if (!IsOperationCurrent(permit))
            {
                return LowPowerSnapshot();
            }

            if (port is not { } activePort)
            {
                return Snapshot(
                    CoolingPadConnectionState.ObservationFailed,
                    fresh: false,
                    CoolingPadObservationFailure.IdentityOrAccess);
            }

            HidSessionResult<CoolingPadFanObservation> observation =
                await activePort.ObserveFanAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!IsOperationCurrent(permit))
            {
                await DropPortAsync().ConfigureAwait(false);
                return LowPowerSnapshot();
            }

            if (observation.Failure == HidSessionFailureKind.Exhausted)
            {
                // ProductionCoolingPadPort guarantees Exhausted before it
                // invokes the HID session. Rotate and repeat this read-only
                // getter once; no other failure or future write may use this
                // recovery path.
                await DropPortAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return Snapshot(
                        CoolingPadConnectionState.ObservationFailed,
                        fresh: false,
                        CoolingPadObservationFailure.Canceled);
                }

                if (!IsOperationCurrent(permit))
                {
                    return LowPowerSnapshot();
                }

                CoolingPadConnectionState replacementConnection =
                    discovery.Observe();
                if (replacementConnection != CoolingPadConnectionState.PresentExact)
                {
                    if (replacementConnection is CoolingPadConnectionState.Absent
                        or CoolingPadConnectionState.Ambiguous)
                    {
                        lastOpaqueFanObservation = null;
                    }

                    return Snapshot(
                        replacementConnection,
                        fresh: replacementConnection
                            != CoolingPadConnectionState.ObservationFailed,
                        replacementConnection
                            == CoolingPadConnectionState.ObservationFailed
                            ? CoolingPadObservationFailure.Discovery
                            : CoolingPadObservationFailure.None);
                }

                HidSessionResult<IProductionCoolingPadPort> replacement =
                    await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (replacement.Failure is not null || replacement.Value is null)
                {
                    if (replacement.Value is not null)
                    {
                        await DisposeIgnoringFailureAsync(replacement.Value)
                            .ConfigureAwait(false);
                    }

                    return Snapshot(
                        CoolingPadConnectionState.ObservationFailed,
                        fresh: false,
                        MapFailure(replacement.Failure));
                }

                port = replacement.Value;
                if (!IsOperationCurrent(permit))
                {
                    await DropPortAsync().ConfigureAwait(false);
                    return LowPowerSnapshot();
                }

                activePort = replacement.Value;
                observation = await activePort.ObserveFanAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!IsOperationCurrent(permit))
                {
                    await DropPortAsync().ConfigureAwait(false);
                    return LowPowerSnapshot();
                }
            }

            if (observation.Failure is null)
            {
                lastOpaqueFanObservation = observation.Value.OpaqueValue;
                lastSuccessfulObservationTimestamp = timeProvider.GetTimestamp();
                return Snapshot(
                    CoolingPadConnectionState.PresentExact,
                    fresh: true,
                    CoolingPadObservationFailure.None);
            }

            if (observation.Failure != HidSessionFailureKind.QueuedCanceled)
            {
                await DropPortAsync().ConfigureAwait(false);
            }

            return Snapshot(
                CoolingPadConnectionState.ObservationFailed,
                fresh: false,
                MapFailure(observation.Failure));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DropPortAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DropPortAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private CoolingPadObservationSnapshot Snapshot(
        CoolingPadConnectionState connection,
        bool fresh,
        CoolingPadObservationFailure failure) => new(
            connection,
            lastOpaqueFanObservation,
            fresh,
            failure);

    private CoolingPadObservationSnapshot LowPowerSnapshot() => Snapshot(
        CoolingPadConnectionState.ObservationFailed,
        fresh: false,
        CoolingPadObservationFailure.LowPower);

    private bool TryAcquireOperation(out LowPowerOperationPermit permit)
    {
        if (lowPowerGate is null)
        {
            permit = default;
            return true;
        }

        return lowPowerGate.TryAcquireOperation(out permit);
    }

    private bool IsOperationCurrent(LowPowerOperationPermit permit) =>
        lowPowerGate is null || lowPowerGate.IsCurrent(permit);

    private async Task DropPortAsync()
    {
        IProductionCoolingPadPort? owned = port;
        port = null;
        lastSuccessfulObservationTimestamp = 0;
        if (owned is not null)
        {
            await DisposeIgnoringFailureAsync(owned).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeIgnoringFailureAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static CoolingPadObservationFailure MapFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked =>
                CoolingPadObservationFailure.IdentityOrAccess,
            HidSessionFailureKind.ConflictBlocked or
            HidSessionFailureKind.ConflictDetectionFailed =>
                CoolingPadObservationFailure.Conflict,
            HidSessionFailureKind.ProtocolFailure =>
                CoolingPadObservationFailure.Protocol,
            HidSessionFailureKind.Canceled or
            HidSessionFailureKind.QueuedCanceled =>
                CoolingPadObservationFailure.Canceled,
            HidSessionFailureKind.Disposed =>
                CoolingPadObservationFailure.Disposed,
            _ => CoolingPadObservationFailure.Transport,
        };

    private static BladeWriteException WriteUnavailable(
        bool ownedOperation,
        string message) => new(
        ownedOperation
            ? WriteSubmissionState.Indeterminate
            : WriteSubmissionState.KnownNotApplied,
        message);

    private static bool IsControllerAuthorityFailure(
        HidSessionFailureKind? failure) => failure is
        HidSessionFailureKind.ConflictBlocked
        or HidSessionFailureKind.ConflictDetectionFailed
        or HidSessionFailureKind.IdentityBlocked;

    private static ICoolingPadHostControlExecutorAdapter? CreateProtocolFanExecutor(
        IProductionCoolingPadPort port) => port is ProductionCoolingPadPort production
            ? new ProtocolCoolingPadHostControlExecutorAdapter(
                production.CreateDormantHostControlExecutor())
            : null;

    private static ICoolingPadLightingExecutorAdapter? CreateProtocolLightingExecutor(
        IProductionCoolingPadPort port) => port is ProductionCoolingPadPort production
            ? new ProtocolCoolingPadLightingExecutorAdapter(
                production.CreateDormantLightingExecutor())
            : null;

    private static CoolingPadLightingWriteOutcome LightingKnownNotApplied() =>
        CoolingPadLightingWriteOutcome.KnownNotApplied;

    private static CoolingPadLightingBrightnessObservation
        LightingQueryKnownNotApplied() => new(
            Raw: null,
            CoolingPadLightingWriteOutcome.KnownNotApplied);

    private enum CoolingPadFanWriteKind
    {
        Acquire,
        Update,
        Release,
    }
}

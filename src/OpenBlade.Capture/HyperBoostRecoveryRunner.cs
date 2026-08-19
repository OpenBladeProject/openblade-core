using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum HyperBoostRecoveryState
{
    Unknown,
    BalancedAutomatic,
    HyperBoostActive,
}

internal enum HyperBoostRecoveryFailure
{
    None,
    OpenFailed,
    CapabilityUnavailable,
    QueryFailed,
    StateNotRecoverable,
    RestoreKnownNotApplied,
    RestoreIndeterminate,
    Canceled,
}

internal sealed record HyperBoostRecoveryResult(
    HyperBoostRecoveryState InitialState,
    bool RestoreAttempted,
    bool BalancedRestorationConfirmed,
    bool ManualRecoveryRequired,
    HyperBoostRecoveryFailure Failure)
{
    internal bool Success => Failure == HyperBoostRecoveryFailure.None;
}

internal sealed class HyperBoostRecoveryRunner(IProductionBatteryPortFactory factory)
{
    private readonly IProductionBatteryPortFactory factory = factory
        ?? throw new ArgumentNullException(nameof(factory));

    internal async Task<HyperBoostRecoveryResult> QueryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HidSessionResult<IProductionBatteryPort> opened =
            await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Failure(cancellationToken.IsCancellationRequested
                ? HyperBoostRecoveryFailure.Canceled
                : HyperBoostRecoveryFailure.OpenFailed);
        }

        await using IProductionBatteryPort port = opened.Value;
        if (port.Capabilities.HyperBoostAccess is not
            (DeviceFeatureAccess.ReadOnly or DeviceFeatureAccess.ReadWrite))
        {
            return Failure(HyperBoostRecoveryFailure.CapabilityUnavailable);
        }

        return await QueryCoreAsync(port, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<HyperBoostRecoveryResult> RestoreBalancedAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HidSessionResult<IProductionBatteryPort> opened =
            await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Failure(cancellationToken.IsCancellationRequested
                ? HyperBoostRecoveryFailure.Canceled
                : HyperBoostRecoveryFailure.OpenFailed);
        }

        await using IProductionBatteryPort port = opened.Value;
        if (port.Capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite)
        {
            return Failure(HyperBoostRecoveryFailure.CapabilityUnavailable);
        }

        HyperBoostRecoveryResult initial = await QueryCoreAsync(
            port,
            cancellationToken).ConfigureAwait(false);
        if (!initial.Success)
        {
            return initial;
        }

        if (initial.InitialState == HyperBoostRecoveryState.BalancedAutomatic)
        {
            return new(
                initial.InitialState,
                RestoreAttempted: false,
                BalancedRestorationConfirmed: true,
                ManualRecoveryRequired: false,
                HyperBoostRecoveryFailure.None);
        }

        if (initial.InitialState != HyperBoostRecoveryState.HyperBoostActive)
        {
            return new(
                initial.InitialState,
                RestoreAttempted: false,
                BalancedRestorationConfirmed: false,
                ManualRecoveryRequired: true,
                HyperBoostRecoveryFailure.StateNotRecoverable);
        }

        try
        {
            await port.SetHyperBoostAsync(false, cancellationToken).ConfigureAwait(false);
            return new(
                initial.InitialState,
                RestoreAttempted: true,
                BalancedRestorationConfirmed: true,
                ManualRecoveryRequired: false,
                HyperBoostRecoveryFailure.None);
        }
        catch (BladeWriteException exception)
        {
            bool indeterminate =
                exception.SubmissionState == WriteSubmissionState.Indeterminate;
            return new(
                initial.InitialState,
                RestoreAttempted: true,
                BalancedRestorationConfirmed: false,
                ManualRecoveryRequired: indeterminate,
                indeterminate
                    ? HyperBoostRecoveryFailure.RestoreIndeterminate
                    : HyperBoostRecoveryFailure.RestoreKnownNotApplied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(
                initial.InitialState,
                RestoreAttempted: true,
                BalancedRestorationConfirmed: false,
                ManualRecoveryRequired: true,
                HyperBoostRecoveryFailure.Canceled);
        }
        catch
        {
            return new(
                initial.InitialState,
                RestoreAttempted: true,
                BalancedRestorationConfirmed: false,
                ManualRecoveryRequired: true,
                HyperBoostRecoveryFailure.RestoreIndeterminate);
        }
    }

    internal async Task<AdapterPowerState> QueryAdapterPowerStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HidSessionResult<IProductionBatteryPort> opened =
            await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return AdapterPowerState.Unknown;
        }

        await using IProductionBatteryPort port = opened.Value;
        try
        {
            return await port.QueryAdapterPowerStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return AdapterPowerState.Unknown;
        }
    }

    private static async Task<HyperBoostRecoveryResult> QueryCoreAsync(
        IProductionBatteryPort port,
        CancellationToken cancellationToken)
    {
        try
        {
            ProductionPerformanceFanState state =
                await port.QueryPerformanceFanStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            HyperBoostRecoveryState recoveryState = state switch
            {
                { HyperBoostActive: true, Performance: null, Fans: FanMode.Automatic } =>
                    HyperBoostRecoveryState.HyperBoostActive,
                {
                    HyperBoostActive: false,
                    Performance: PerformancePreset.Balanced,
                    Fans: FanMode.Automatic,
                } => HyperBoostRecoveryState.BalancedAutomatic,
                _ => HyperBoostRecoveryState.Unknown,
            };
            if (recoveryState == HyperBoostRecoveryState.Unknown)
            {
                return new(
                    recoveryState,
                    RestoreAttempted: false,
                    BalancedRestorationConfirmed: false,
                    ManualRecoveryRequired: true,
                    HyperBoostRecoveryFailure.StateNotRecoverable);
            }

            return new(
                recoveryState,
                RestoreAttempted: false,
                BalancedRestorationConfirmed:
                    recoveryState == HyperBoostRecoveryState.BalancedAutomatic,
                ManualRecoveryRequired: false,
                HyperBoostRecoveryFailure.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(HyperBoostRecoveryFailure.Canceled);
        }
        catch
        {
            return Failure(HyperBoostRecoveryFailure.QueryFailed);
        }
    }

    private static HyperBoostRecoveryResult Failure(
        HyperBoostRecoveryFailure failure) => new(
        HyperBoostRecoveryState.Unknown,
        RestoreAttempted: false,
        BalancedRestorationConfirmed: false,
        ManualRecoveryRequired: failure is
            HyperBoostRecoveryFailure.RestoreIndeterminate
            or HyperBoostRecoveryFailure.StateNotRecoverable,
        failure);
}

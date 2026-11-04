using OpenBlade.Contracts;
using OpenBlade.Display;

namespace OpenBlade.Session;

public sealed record SessionDisplayExecutionResult(
    SessionDisplayOperationStatus Status,
    string? Error,
    DisplaySettingsSnapshot State)
{
    public bool Succeeded => Status == SessionDisplayOperationStatus.Succeeded;
}

public interface IDisplayProfileApplier
{
    Task<SessionDisplayExecutionResult> QueryAsync(CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
        PowerSource expectedPowerSource,
        string refreshRateKey,
        CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> SetHdrAsync(
        PowerSource expectedPowerSource,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> SetColorProfileAsync(
        PowerSource expectedPowerSource,
        string colorProfileKey,
        CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> SetBrightnessAsync(
        PowerSource expectedPowerSource,
        byte percent,
        CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken = default);

    Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
        PowerSource expectedPowerSource,
        long expectedGeneration,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken);
}

public sealed class DisplayProfileApplier : IDisplayProfileApplier, IDisposable
{
    private readonly IDisplaySettingsController controller;
    private readonly IDisplayPowerContextSource powerContext;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int disposed;

    public DisplayProfileApplier(
        IDisplaySettingsController controller,
        IDisplayPowerContextSource powerContext)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.powerContext = powerContext ?? throw new ArgumentNullException(nameof(powerContext));
    }

    public Task<SessionDisplayExecutionResult> QueryAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(
            async token => Success(await controller.QueryAsync(token).ConfigureAwait(false)),
            cancellationToken);

    public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
        PowerSource expectedPowerSource,
        string refreshRateKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshRateKey);
        return RunExpectedAsync(
            expectedPowerSource,
            (context, token) => ExecuteSetterAsync(
                context,
                token,
                operationToken => controller.SetRefreshAsync(
                    refreshRateKey,
                    operationToken)),
            cancellationToken);
    }

    public Task<SessionDisplayExecutionResult> SetHdrAsync(
        PowerSource expectedPowerSource,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunExpectedAsync(
            expectedPowerSource,
            (context, token) => ExecuteSetterAsync(
                context,
                token,
                operationToken => controller.SetHdrAsync(enabled, operationToken)),
            cancellationToken);

    public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
        PowerSource expectedPowerSource,
        string colorProfileKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colorProfileKey);
        return RunExpectedAsync(
            expectedPowerSource,
            (context, token) => ExecuteSetterAsync(
                context,
                token,
                operationToken => controller.SetColorProfileAsync(
                    colorProfileKey,
                    operationToken)),
            cancellationToken);
    }

    public Task<SessionDisplayExecutionResult> SetBrightnessAsync(
        PowerSource expectedPowerSource,
        byte percent,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, (byte)100);
        return RunExpectedAsync(
            expectedPowerSource,
            async (context, token) =>
            {
                DisplaySettingsSnapshot state = await controller.QueryAsync(token)
                    .ConfigureAwait(false);
                if (!state.IsAvailable || state.SupportedBrightnessLevels.Count == 0)
                {
                    return Failure("Display brightness is unavailable.", state);
                }
                byte target = DisplayBrightnessLevels.SelectNearest(
                    percent,
                    state.SupportedBrightnessLevels);
                if (state.BrightnessPercent == target)
                {
                    return Success(state);
                }
                SessionDisplayExecutionResult result = await ExecuteSetterAsync(
                    context,
                    token,
                    operationToken => controller.SetBrightnessAsync(target, operationToken))
                    .ConfigureAwait(false);
                return result.Succeeded && result.State.BrightnessPercent != target
                    ? Failure(
                        "Windows did not confirm the requested display brightness.",
                        result.State,
                        submissionMayHaveOccurred: true)
                    : result;
            },
            cancellationToken);
    }

    public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RunExpectedAsync(
            expectedPowerSource,
            (context, token) => ApplyProfileCoreAsync(context, settings, token),
            cancellationToken,
            allowUnconfiguredNoOp: !settings.IsConfigured);
    }

    public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
        PowerSource expectedPowerSource,
        long expectedGeneration,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RunSerializedAsync(
            token => ApplyProfileWithContextAsync(
                new DisplayPowerContext(expectedPowerSource, expectedGeneration),
                settings,
                token,
                allowUnconfiguredNoOp: !settings.IsConfigured),
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        operationGate.Dispose();
    }

    private Task<SessionDisplayExecutionResult> RunExpectedAsync(
        PowerSource expectedPowerSource,
        Func<DisplayPowerContext, CancellationToken, Task<SessionDisplayExecutionResult>> operation,
        CancellationToken cancellationToken,
        bool allowUnconfiguredNoOp = false) =>
        RunSerializedAsync(
            async token =>
            {
                DisplayPowerContext context = powerContext.Capture();
                if (!allowUnconfiguredNoOp)
                {
                    SessionDisplayExecutionResult? blocked = await ValidateExpectedAsync(
                        expectedPowerSource,
                        context,
                        token).ConfigureAwait(false);
                    if (blocked is not null)
                    {
                        return blocked;
                    }
                }

                return await operation(
                    new DisplayPowerContext(expectedPowerSource, context.Generation),
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<SessionDisplayExecutionResult> ApplyProfileWithContextAsync(
        DisplayPowerContext expected,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken,
        bool allowUnconfiguredNoOp)
    {
        if (!allowUnconfiguredNoOp)
        {
            SessionDisplayExecutionResult? blocked = await ValidateExpectedAsync(
                expected.PowerSource,
                expected,
                cancellationToken).ConfigureAwait(false);
            if (blocked is not null)
            {
                return blocked;
            }
        }

        return await ApplyProfileCoreAsync(expected, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SessionDisplayExecutionResult> ApplyProfileCoreAsync(
        DisplayPowerContext expected,
        DisplayProfileSettings settings,
        CancellationToken cancellationToken)
    {
        DisplaySettingsSnapshot state = await controller.QueryAsync(cancellationToken)
            .ConfigureAwait(false);
        bool submitted = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.IsConfigured)
        {
            return Success(state);
        }
        if (!state.IsAvailable)
        {
            return Failure(state.Message ?? "Display settings are unavailable.", state);
        }

        if (settings.RefreshRateKey is not null
            && !string.Equals(
                state.CurrentRefreshKey,
                settings.RefreshRateKey,
                StringComparison.Ordinal))
        {
            EnsureCurrent(expected, cancellationToken);
            DisplayOperationResult result = await controller.SetRefreshAsync(
                settings.RefreshRateKey,
                cancellationToken).ConfigureAwait(false);
            submitted |= result.Succeeded || result.SubmissionMayHaveOccurred;
            state = await ResolveStateAsync(result, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return Failure(result.Error ?? "Refresh-rate change failed.", state,
                    submitted);
            }
            EnsureCurrent(expected, cancellationToken);
        }

        if (settings.HdrEnabled.HasValue
            && state.HdrSupported
            && state.HdrEnabled != settings.HdrEnabled.Value)
        {
            EnsureCurrent(expected, cancellationToken);
            DisplayOperationResult result = await controller.SetHdrAsync(
                settings.HdrEnabled.Value,
                cancellationToken).ConfigureAwait(false);
            submitted |= result.Succeeded || result.SubmissionMayHaveOccurred;
            state = await ResolveStateAsync(result, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return Failure(result.Error ?? "HDR change failed.", state,
                    submitted);
            }
            EnsureCurrent(expected, cancellationToken);
        }

        if (settings.ColorProfileKey is not null
            && !state.HdrEnabled
            && !string.Equals(
                state.ActiveColorProfileId,
                settings.ColorProfileKey,
                StringComparison.OrdinalIgnoreCase))
        {
            EnsureCurrent(expected, cancellationToken);
            DisplayOperationResult result = await controller.SetColorProfileAsync(
                settings.ColorProfileKey,
                cancellationToken).ConfigureAwait(false);
            submitted |= result.Succeeded || result.SubmissionMayHaveOccurred;
            state = await ResolveStateAsync(result, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return Failure(result.Error ?? "Color-profile change failed.", state,
                    submitted);
            }
            EnsureCurrent(expected, cancellationToken);
        }

        if (settings.BrightnessPercent.HasValue)
        {
            if (state.SupportedBrightnessLevels.Count == 0)
            {
                return Failure("Display brightness is unavailable.", state);
            }
            byte target = DisplayBrightnessLevels.SelectNearest(
                settings.BrightnessPercent.Value,
                state.SupportedBrightnessLevels);
            if (state.BrightnessPercent != target)
            {
                EnsureCurrent(expected, cancellationToken);
                DisplayOperationResult result = await controller.SetBrightnessAsync(
                    target,
                    cancellationToken).ConfigureAwait(false);
                submitted |= result.Succeeded || result.SubmissionMayHaveOccurred;
                state = await ResolveStateAsync(result, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return Failure(result.Error ?? "Brightness change failed.", state,
                        submitted);
                }
                EnsureCurrent(expected, cancellationToken);
            }
        }

        return ProfileMatches(settings, state)
            ? Success(state)
            : Failure(
                "Windows did not confirm every requested display setting.",
                state,
                submitted);
    }

    private async Task<SessionDisplayExecutionResult> ExecuteSetterAsync(
        DisplayPowerContext expected,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<DisplayOperationResult>> setter)
    {
        EnsureCurrent(expected, cancellationToken);
        DisplayOperationResult result = await setter(cancellationToken).ConfigureAwait(false);
        DisplaySettingsSnapshot state = await ResolveStateAsync(result, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded)
        {
            return Failure(result.Error ?? "Display setting failed.", state,
                result.SubmissionMayHaveOccurred);
        }
        EnsureCurrent(expected, cancellationToken);
        return Success(state);
    }

    private async Task<SessionDisplayExecutionResult?> ValidateExpectedAsync(
        PowerSource expectedPowerSource,
        DisplayPowerContext expectedContext,
        CancellationToken cancellationToken)
    {
        DisplayPowerContext current = powerContext.Capture();
        if (expectedPowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
            || !current.IsConfirmed
            || current.PowerSource != expectedPowerSource
            || current.Generation != expectedContext.Generation)
        {
            DisplaySettingsSnapshot state = await controller.QueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return Blocked(
                !current.IsConfirmed
                    ? "Display changes are blocked until the power source is confirmed."
                    : "The active power source changed before the display operation began.",
                state);
        }
        return null;
    }

    private void EnsureCurrent(
        DisplayPowerContext expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisplayPowerContext current = powerContext.Capture();
        if (!current.IsConfirmed
            || current.PowerSource != expected.PowerSource
            || current.Generation != expected.Generation)
        {
            throw new OperationCanceledException(
                "The display operation became stale after the power source changed.",
                cancellationToken);
        }
    }

    private async Task<DisplaySettingsSnapshot> ResolveStateAsync(
        DisplayOperationResult result,
        CancellationToken cancellationToken) =>
        result.State ?? await controller.QueryAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionDisplayExecutionResult> RunSerializedAsync(
        Func<CancellationToken, Task<SessionDisplayExecutionResult>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static bool ProfileMatches(
        DisplayProfileSettings settings,
        DisplaySettingsSnapshot state) =>
        state.IsAvailable
        && (settings.RefreshRateKey is null
            || string.Equals(
                settings.RefreshRateKey,
                state.CurrentRefreshKey,
                StringComparison.Ordinal))
        && (!settings.HdrEnabled.HasValue || settings.HdrEnabled == state.HdrEnabled)
        && (settings.ColorProfileKey is null
            || state.HdrEnabled
            || string.Equals(
                settings.ColorProfileKey,
                state.ActiveColorProfileId,
                StringComparison.OrdinalIgnoreCase))
        && (!settings.BrightnessPercent.HasValue
            || state.SupportedBrightnessLevels.Count > 0
            && state.BrightnessPercent == DisplayBrightnessLevels.SelectNearest(
                settings.BrightnessPercent.Value,
                state.SupportedBrightnessLevels));

    private static SessionDisplayExecutionResult Success(DisplaySettingsSnapshot state) =>
        new(SessionDisplayOperationStatus.Succeeded, null, state);

    private static SessionDisplayExecutionResult Blocked(
        string error,
        DisplaySettingsSnapshot state) =>
        new(SessionDisplayOperationStatus.Blocked, error, state);

    private static SessionDisplayExecutionResult Failure(
        string error,
        DisplaySettingsSnapshot state,
        bool submissionMayHaveOccurred = false) =>
        new(submissionMayHaveOccurred
                ? SessionDisplayOperationStatus.Indeterminate
                : SessionDisplayOperationStatus.Failed,
            error,
            state);
}

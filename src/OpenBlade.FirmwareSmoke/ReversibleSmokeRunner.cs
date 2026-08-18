using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public sealed class ReversibleSmokeRunner(IFirmwareSmokeClient client)
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<IReadOnlyList<FirmwareSmokeCheck>> RunAsync(
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot original = await client.ObserveStateAsync(
            LiveReadingSensors.All,
            cancellationToken).ConfigureAwait(false);
        ValidatePreconditions(original);

        var checks = new List<FirmwareSmokeCheck>();
        Func<Task<FirmwareSmokeCheck>>[] operations =
        {
            () => TestPerformanceAsync(original, cancellationToken),
            () => TestHyperBoostAsync(original, cancellationToken),
            () => TestFansAsync(original, cancellationToken),
            () => TestBatteryAsync(original, cancellationToken),
            () => TestLightingAsync(original, cancellationToken),
            () => TestStartupAnimationAsync(original, cancellationToken),
            () => TestFunctionKeyPrimaryAsync(original, cancellationToken),
            () => TestGamingModeAsync(original, cancellationToken),
        };
        foreach (Func<Task<FirmwareSmokeCheck>> operation in operations)
        {
            FirmwareSmokeCheck check = await operation().ConfigureAwait(false);
            checks.Add(check);
            if (check.Outcome == FirmwareSmokeOutcome.Failed)
            {
                break;
            }
        }

        return checks;
    }

    private async Task<FirmwareSmokeCheck> TestHyperBoostAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        const string id = "write.hyper-boost";
        PerformancePreset? baseMode = original.ConfirmedState?.Performance;
        string baseLabel = baseMode == PerformancePreset.Silent
            ? "Quiet"
            : baseMode?.ToString() ?? "base mode";
        string description =
            $"HyperBoost applies, reads back, and restores {baseLabel} directly";
        if (original.Capabilities is not { } capabilities
            || capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
            || baseMode is null
            || (capabilities.HyperBoostBaseModes?.AccessFor(baseMode.Value)
                ?? (baseMode == PerformancePreset.Balanced
                    ? capabilities.HyperBoostAccess
                    : null)) != DeviceFeatureAccess.ReadWrite
            || original.ConfirmedState is not
            {
                Performance: not null,
                Fans: FanMode.Automatic,
                HyperBoostActive: false,
            })
        {
            return Skipped(
                id,
                description,
                "A confirmed writable base mode with Automatic fans and inactive HyperBoost is required.");
        }

        bool padPresent = original.CoolingPad is
        {
            Connection: CoolingPadConnectionState.PresentExact,
            DesiredFanIntent.Mode: CoolingPadFanMode.Automatic,
        };
        bool padAbsentAdmitted =
            baseMode == PerformancePreset.Balanced
            && capabilities.HyperBoostWithoutCoolingPadAccess
                == DeviceFeatureAccess.ReadWrite
            && original.CoolingPad?.Connection == CoolingPadConnectionState.Absent;
        if (!padPresent && !padAbsentAdmitted)
        {
            return Skipped(
                id,
                description,
                "An exact pad with saved Auto intent or admitted pad-less Balanced support with confirmed pad absence is required.");
        }

        string application = "Not attempted";
        string restoration = "Not attempted";
        bool applyAttempted = false;
        bool applicationAccepted = false;
        bool applicationPassed = false;
        bool restorationPassed = false;
        try
        {
            applyAttempted = true;
            ControlOperationResult result = await client.SetHyperBoostAsync(
                enabled: true,
                cancellationToken).ConfigureAwait(false);
            application = $"{result.Status}: {result.Message}";
            applicationAccepted = IsAccepted(result);
            if (!applicationAccepted)
            {
                application += "; no retry was attempted.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            application = $"{exception.GetType().Name}: {exception.Message}; no retry was attempted.";
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            bool active = false;
            if (applyAttempted)
            {
                try
                {
                    if (applicationAccepted)
                    {
                        active = await WaitForAsync(
                            status => status.ConfirmedState?.HyperBoostActive == true,
                            cleanup.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        ServiceStatusSnapshot inspected =
                            await client.ObserveStateAsync(
                                LiveReadingSensors.None,
                                cleanup.Token).ConfigureAwait(false);
                        active = inspected.ConfirmedState?.HyperBoostActive == true;
                    }
                }
                catch (Exception exception)
                {
                    application +=
                        $" Inspection={exception.GetType().Name}: {exception.Message}";
                }
            }

            applicationPassed = applicationAccepted && active;
            if (!active)
            {
                application +=
                    "; independent readback did not confirm HyperBoost, so no restoration write was submitted.";
            }
            else
            {
                try
                {
                    ControlOperationResult result = await client.SetHyperBoostAsync(
                        enabled: false,
                        cleanup.Token).ConfigureAwait(false);
                    restoration = $"{result.Status}: {result.Message}";
                    restorationPassed = IsAccepted(result)
                        && await WaitForAsync(
                            status => status.ConfirmedState is
                            {
                                HyperBoostActive: false,
                                Performance: { } restoredPerformance,
                                Fans: FanMode.Automatic,
                            } && restoredPerformance == baseMode,
                            cleanup.Token).ConfigureAwait(false);
                    if (!restorationPassed)
                    {
                        restoration +=
                            $"; independent readback did not confirm the exact {baseLabel} baseline.";
                    }
                }
                catch (Exception exception)
                {
                    restoration = $"{exception.GetType().Name}: {exception.Message}";
                }
            }
        }

        return new FirmwareSmokeCheck(
            id,
            description,
            applicationPassed && restorationPassed
                ? FirmwareSmokeOutcome.Passed
                : FirmwareSmokeOutcome.Failed,
            $"Mode 7 confirmed; {baseLabel} and Automatic restored",
            $"Apply={application} Restore={restoration}");
    }

    private async Task<FirmwareSmokeCheck> TestPerformanceAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        PerformancePreset? originalValue = original.ConfirmedState?.Performance;
        if (originalValue is null || original.Capabilities is null)
        {
            return Skipped("write.performance", "Performance preset", "Readback unavailable.");
        }

        PerformancePreset candidate = originalValue == PerformancePreset.Balanced
            ? PerformancePreset.Silent
            : PerformancePreset.Balanced;
        if (!original.Capabilities.PerformancePresets.Contains(candidate))
        {
            return Skipped("write.performance", "Performance preset", $"{candidate} is unavailable.");
        }

        return await RunRestoredAsync(
            "write.performance",
            "Performance preset applies, reads back, and restores",
            () => client.SetPerformanceOverrideAsync(
                new PerformanceOverride(
                    candidate,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            status => status.ConfirmedState?.Performance == candidate,
            restoreToken => RestorePerformanceAsync(
                original,
                originalValue.Value,
                restoreToken),
            status => status.ConfirmedState?.Performance == originalValue,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirmwareSmokeCheck> TestFansAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        FanMode? originalMode = original.ConfirmedState?.Fans;
        if (originalMode is null
            || original.Capabilities is null
            || !original.Capabilities.FanModes.Contains(FanMode.Fixed))
        {
            return Skipped("write.fans", "Fixed fan mode", "Required readback or capability is unavailable.");
        }

        var settings = new FanControlSettings(
            FanMode.Fixed,
            fixedSpeedRpm: 3000,
            ManualFanCurve.Default);
        return await RunRestoredAsync(
            "write.fans",
            "Fixed fan mode applies, reads back, and restores",
            () => client.SetFanOverrideAsync(
                new ManualOverride<FanMode>(
                    FanMode.Fixed,
                    OverrideLifetime.UntilPowerSourceChange),
                settings,
                cancellationToken),
            status => status.FanControlOverride is
            {
                Mode: FanMode.Fixed,
                FixedSpeedRpm: 3000,
            },
            restoreToken => client.SetFanOverrideAsync(
                original.FanOverride,
                original.FanControlOverride,
                restoreToken),
            status => status.ConfirmedState?.Fans == originalMode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirmwareSmokeCheck> TestBatteryAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        ConfirmedBatteryState? battery = original.ConfirmedState?.Battery;
        if (battery is not
            {
                ProtectionEnabled: true,
                ChargeLimitPercent: { } originalLimit,
                TemporaryFullCharge: false,
            }
            || original.Capabilities is null)
        {
            return Skipped(
                "write.battery",
                "Charge limit",
                "Battery protection must be enabled with temporary full charge off.");
        }

        int candidate = originalLimit == 80 ? 75 : originalLimit + 5;
        if (!original.Capabilities.ChargeLimits.Contains(candidate))
        {
            return Skipped("write.battery", "Charge limit", $"{candidate}% is unavailable.");
        }

        return await RunRestoredAsync(
            "write.battery",
            "Charge limit applies, reads back, and restores",
            () => client.SetBatteryPolicyAsync(
                BatteryPolicy.Create(candidate),
                cancellationToken),
            status => status.ConfirmedState?.Battery?.ChargeLimitPercent == candidate,
            restoreToken => client.SetBatteryPolicyAsync(
                BatteryPolicy.Create(originalLimit),
                restoreToken),
            status => status.ConfirmedState?.Battery?.ChargeLimitPercent == originalLimit,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirmwareSmokeCheck> TestLightingAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        if (original.Capabilities is null
            || !original.Capabilities.LightingEffects.Contains(LightingEffect.Static))
        {
            return Skipped("write.lighting", "Keyboard lighting", "Static lighting is unavailable.");
        }

        LightingRendererOwnershipState originalRenderer = original.LightingRendererOwnership;
        LightingSettings candidate = LightingSettingsFactory.Static(
            new RgbColor(0x47, 0xE1, 0x0C),
            brightness: 15) with
        {
            LogoMode = original.ConfirmedState?.LogoLighting,
        };
        return await RunRestoredAsync(
            "write.lighting",
            "Static keyboard lighting applies and the previous renderer restores",
            () => client.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    candidate,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            status => status.LightingRendererOwnership
                == LightingRendererOwnershipState.Static,
            restoreToken => client.SetLightingOverrideAsync(
                original.LightingOverride,
                restoreToken),
            status => status.LightingRendererOwnership == originalRenderer,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<FirmwareSmokeCheck> TestStartupAnimationAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        if (original.StartupAnimationEnabled is not { } originalValue)
        {
            return Task.FromResult(Skipped(
                "write.startup-animation",
                "Startup animation",
                "Readback unavailable."));
        }

        return TestBooleanLaptopControlAsync(
            "write.startup-animation",
            "Startup animation toggles and restores",
            OneShotLaptopControl.StartupAnimation,
            originalValue,
            status => status.StartupAnimationEnabled,
            cancellationToken);
    }

    private Task<FirmwareSmokeCheck> TestGamingModeAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        if (original.GamingModeEnabled is not { } originalValue)
        {
            return Task.FromResult(Skipped(
                "write.gaming-mode",
                "Gaming mode",
                "Readback unavailable."));
        }

        return TestBooleanLaptopControlAsync(
            "write.gaming-mode",
            "Gaming mode toggles and restores",
            OneShotLaptopControl.GamingMode,
            originalValue,
            status => status.GamingModeEnabled,
            cancellationToken);
    }

    private async Task<FirmwareSmokeCheck> TestFunctionKeyPrimaryAsync(
        ServiceStatusSnapshot original,
        CancellationToken cancellationToken)
    {
        if (original.FunctionKeyPrimary is not { } originalValue)
        {
            return Skipped(
                "write.function-key-primary",
                "Function-key primary mode",
                "Readback unavailable.");
        }

        FunctionKeyPrimaryMode candidate =
            originalValue == FunctionKeyPrimaryMode.FunctionKeys
                ? FunctionKeyPrimaryMode.MultimediaKeys
                : FunctionKeyPrimaryMode.FunctionKeys;
        return await RunRestoredAsync(
            "write.function-key-primary",
            "Function-key primary mode toggles and restores",
            () => client.ApplyLaptopControlAsync(
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: candidate),
                cancellationToken),
            status => status.FunctionKeyPrimary == candidate,
            restoreToken => client.ApplyLaptopControlAsync(
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: originalValue),
                restoreToken),
            status => status.FunctionKeyPrimary == originalValue,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<FirmwareSmokeCheck> TestBooleanLaptopControlAsync(
        string id,
        string description,
        OneShotLaptopControl control,
        bool originalValue,
        Func<ServiceStatusSnapshot, bool?> selector,
        CancellationToken cancellationToken) =>
        RunRestoredAsync(
            id,
            description,
            () => client.ApplyLaptopControlAsync(
                new ApplyOneShotLaptopControl(control, Enabled: !originalValue),
                cancellationToken),
            status => selector(status) == !originalValue,
            restoreToken => client.ApplyLaptopControlAsync(
                new ApplyOneShotLaptopControl(control, Enabled: originalValue),
                restoreToken),
            status => selector(status) == originalValue,
            cancellationToken);

    private async Task<FirmwareSmokeCheck> RunRestoredAsync(
        string id,
        string description,
        Func<Task<ControlOperationResult>> applyAsync,
        Func<ServiceStatusSnapshot, bool> appliedPredicate,
        Func<CancellationToken, Task<ControlOperationResult>> restoreAsync,
        Func<ServiceStatusSnapshot, bool> restoredPredicate,
        CancellationToken cancellationToken)
    {
        string application = "Not attempted";
        string restoration = "Not attempted";
        bool applicationPassed = false;
        bool restorationPassed = false;
        try
        {
            ControlOperationResult result = await applyAsync().ConfigureAwait(false);
            application = $"{result.Status}: {result.Message}";
            if (!IsAccepted(result)
                && IsSafePreWriteRetry(result)
                && await IsCurrentlyAsync(
                    restoredPredicate,
                    cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    cancellationToken).ConfigureAwait(false);
                ControlOperationResult retry = await applyAsync().ConfigureAwait(false);
                application += $" Retry={retry.Status}: {retry.Message}";
                result = retry;
            }

            applicationPassed = IsAccepted(result)
                && await WaitForAsync(appliedPredicate, cancellationToken).ConfigureAwait(false);
            if (!applicationPassed)
            {
                application += "; readback did not confirm the candidate.";
            }
            else
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            application = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            using var restoreCancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
            try
            {
                ControlOperationResult result = await restoreAsync(
                    restoreCancellation.Token).ConfigureAwait(false);
                restoration = $"{result.Status}: {result.Message}";
                restorationPassed = IsAccepted(result)
                    && await WaitForAsync(
                        restoredPredicate,
                        restoreCancellation.Token).ConfigureAwait(false);
                if (!restorationPassed)
                {
                    restoration += "; readback did not confirm restoration.";
                }
            }
            catch (Exception exception)
            {
                restoration = $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        return new FirmwareSmokeCheck(
            id,
            description,
            applicationPassed && restorationPassed
                ? FirmwareSmokeOutcome.Passed
                : FirmwareSmokeOutcome.Failed,
            "Candidate confirmed; original state restored",
            $"Apply={application} Restore={restoration}");
    }

    private async Task<bool> WaitForAsync(
        Func<ServiceStatusSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StateTimeout;
        do
        {
            ServiceStatusSnapshot status = await client.ObserveStateAsync(
                LiveReadingSensors.None,
                cancellationToken).ConfigureAwait(false);
            if (predicate(status))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    private async Task<bool> IsCurrentlyAsync(
        Func<ServiceStatusSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot status = await client.ObserveStateAsync(
            LiveReadingSensors.None,
            cancellationToken).ConfigureAwait(false);
        return status.DeviceVerified
            && !status.WritesBlocked
            && status.PotentialControllers.Count == 0
            && status.ExternalChanges.Count == 0
            && status.UnconfirmedWrites.Count == 0
            && predicate(status);
    }

    private async Task<ControlOperationResult> RestorePerformanceAsync(
        ServiceStatusSnapshot original,
        PerformancePreset originalValue,
        CancellationToken cancellationToken)
    {
        if (original.PerformanceOverride is not null)
        {
            return await client.SetPerformanceOverrideAsync(
                original.PerformanceOverride,
                cancellationToken).ConfigureAwait(false);
        }

        ControlOperationResult explicitRestore =
            await client.SetPerformanceOverrideAsync(
                new PerformanceOverride(
                    originalValue,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken).ConfigureAwait(false);
        if (!await WaitForAsync(
                status => status.ConfirmedState?.Performance == originalValue,
                cancellationToken).ConfigureAwait(false))
        {
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "The original performance preset could not be explicitly restored. " +
                $"Hardware result: {explicitRestore.Status}: {explicitRestore.Message}");
        }

        ControlOperationResult cleared = await client.SetPerformanceOverrideAsync(
            value: null,
            cancellationToken).ConfigureAwait(false);
        bool independentlyCleared = await WaitForAsync(
            status => status.ConfirmedState?.Performance == originalValue
                && status.PerformanceOverride is null,
            cancellationToken).ConfigureAwait(false);
        return independentlyCleared
            ? new ControlOperationResult(
                ControlOperationStatus.Applied,
                IsAccepted(cleared)
                    ? cleared.Message
                    : "The original preset and cleared override were confirmed by independent readback. " +
                    $"Clear result: {cleared.Status}: {cleared.Message}")
            : cleared;
    }

    private static bool IsAccepted(ControlOperationResult result) =>
        result.Status is ControlOperationStatus.Applied
            or ControlOperationStatus.Stored;

    private static bool IsSafePreWriteRetry(ControlOperationResult result) =>
        result.Status == ControlOperationStatus.Failed
        && result.Message.Contains(
            "could not be read before writing",
            StringComparison.OrdinalIgnoreCase)
        && !result.Message.Contains(
            "indeterminate",
            StringComparison.OrdinalIgnoreCase);

    private static FirmwareSmokeCheck Skipped(
        string id,
        string description,
        string reason) =>
        new(
            id,
            description,
            FirmwareSmokeOutcome.Skipped,
            "Capability and readback available",
            reason);

    private static void ValidatePreconditions(ServiceStatusSnapshot status)
    {
        if (!status.DeviceVerified
            || status.WritesBlocked
            || status.PotentialControllers.Count > 0
            || status.ExternalChanges.Count > 0
            || status.UnconfirmedWrites.Count > 0
            || status.ActivePowerSource != PowerSource.PluggedIn)
        {
            throw new InvalidOperationException(
                "Reversible tests require a verified, write-enabled device on the " +
                "full-power AC adapter with no controller conflict or external changes.");
        }

        if (status.LiveReadings?.CpuTemperatureCelsius is > 80)
        {
            throw new InvalidOperationException(
                "Reversible tests are blocked while the CPU is above 80 degrees Celsius.");
        }
    }
}

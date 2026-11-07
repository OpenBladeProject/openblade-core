using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private FanStopSettings observedFanStopSettings = FanStopSettings.Default;
    private int fanStopEndModeIndex;
    private bool synchronizingFanStopSettings;
    private double fanStopCpuLimit = 55, fanStopGpuLimit = 55, fanStopExitSeconds = 5;
    private bool automaticFanStopEnabled;
    private double automaticFanStopCpuLimit = 50, automaticFanStopGpuLimit = 50, automaticFanStopSeconds = 30;

    public int FanStopEndModeIndex
    {
        get => fanStopEndModeIndex;
        set
        {
            if (SetFanStopDraftField(ref fanStopEndModeIndex, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double FanStopCpuLimit
    {
        get => fanStopCpuLimit;
        set
        {
            if (SetFanStopDraftField(ref fanStopCpuLimit, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double FanStopGpuLimit
    {
        get => fanStopGpuLimit;
        set
        {
            if (SetFanStopDraftField(ref fanStopGpuLimit, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double FanStopExitSeconds
    {
        get => fanStopExitSeconds;
        set
        {
            if (SetFanStopDraftField(ref fanStopExitSeconds, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public bool AutomaticFanStopEnabled
    {
        get => automaticFanStopEnabled;
        set
        {
            if (SetFanStopDraftField(ref automaticFanStopEnabled, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double AutomaticFanStopCpuLimit
    {
        get => automaticFanStopCpuLimit;
        set
        {
            if (SetFanStopDraftField(ref automaticFanStopCpuLimit, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double AutomaticFanStopGpuLimit
    {
        get => automaticFanStopGpuLimit;
        set
        {
            if (SetFanStopDraftField(ref automaticFanStopGpuLimit, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public double AutomaticFanStopSeconds
    {
        get => automaticFanStopSeconds;
        set
        {
            if (SetFanStopDraftField(ref automaticFanStopSeconds, value))
            {
                NotifyFanStopSettingsPresentation();
            }
        }
    }

    public bool IsCustomFanStopEnd => FanStopEndModeIndex == 1;
    public bool CanEditFanStopSettings => !synchronizingFanStopSettings && IsFanStopVisible && Capabilities.SupportsFanStop && CanRequestHardwareControl
        && !IsFanControlOperationInProgress && CurrentFanStopStatus.State is not (FanStopState.RecoveryRequired or FanStopState.ReturningToAutomatic);
    public bool CanResumeAutomaticFanStop => CanEditFanStopSettings && CurrentFanStopStatus.Settings.AutomaticEnabled
        && CurrentFanStopStatus.AutomationState == FanStopAutomationState.Paused;
    public bool HasUnsavedFanStopSettings => !TryGetFanStopSettings(out FanStopSettings draft)
        || draft != CurrentFanStopStatus.Settings;
    public string FanStopEndSummary
    {
        get
        {
            FanStopSettings saved = CurrentFanStopStatus.Settings;
            return saved.EndMode == FanStopEndMode.Custom
                ? TrayText.Format("Custom: CPU {0} °C / GPU {1} °C for {2} s", saved.CpuLimitCelsius, saved.GpuLimitCelsius, saved.SecondsAboveLimit)
                : TrayText.Get("Auto: use the existing conservative end conditions.");
        }
    }
    public string AutomaticFanStopSummary => !CurrentFanStopStatus.Settings.AutomaticEnabled
        ? TrayText.Get("Off") : CurrentFanStopStatus.AutomationState == FanStopAutomationState.Paused
        ? TrayText.Get("Paused — resume to start a fresh cool interval.")
        : TrayText.Format("CPU below {0} °C and GPU below {1} °C for {2} s", CurrentFanStopStatus.Settings.AutomaticCpuBelowCelsius,
            CurrentFanStopStatus.Settings.AutomaticGpuBelowCelsius, CurrentFanStopStatus.Settings.SecondsBelowLimits);
    public string FanStopTemperatureWarning => IsCustomFanStopEnd && (FanStopCpuLimit > FanStopSettings.TemperatureWarningCelsius || FanStopGpuLimit > FanStopSettings.TemperatureWarningCelsius)
        ? TrayText.Get("Higher temperature limits allow the laptop to stay hotter before cooling restarts.") : string.Empty;
    public string FanStopDelayWarning => IsCustomFanStopEnd && FanStopExitSeconds > FanStopSettings.DurationWarningSeconds
        ? TrayText.Get("A longer delay keeps the fans stopped while temperatures remain above the limit.") : string.Empty;
    public string AutomaticFanStopCpuError => AutomaticFanStopEnabled
        && AutomaticFanStopCpuLimit >= (IsCustomFanStopEnd ? FanStopCpuLimit : 55)
        ? TrayText.Get("The CPU start limit must be below the CPU end limit.") : string.Empty;
    public string AutomaticFanStopGpuError => AutomaticFanStopEnabled && IsCustomFanStopEnd && AutomaticFanStopGpuLimit >= FanStopGpuLimit
        ? TrayText.Get("The GPU start limit must be below the GPU end limit.") : string.Empty;
    public string FanStopSettingsError => TryGetFanStopSettings(out _) ? string.Empty
        : TrayText.Get("Use whole temperatures from 10 to 100 °C, an end delay from 1 to 300 seconds, and a cool interval from 1 to 3600 seconds. Start limits must be below the corresponding end limits.");
    public bool HasFanStopTemperatureWarning => FanStopTemperatureWarning.Length != 0;
    public bool HasFanStopDelayWarning => FanStopDelayWarning.Length != 0;
    public bool HasAutomaticFanStopCpuError => AutomaticFanStopCpuError.Length != 0;
    public bool HasAutomaticFanStopGpuError => AutomaticFanStopGpuError.Length != 0;
    public bool HasFanStopSettingsError => FanStopSettingsError.Length != 0;

    private bool SetFanStopDraftField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        SetField(ref field, value, name);
        return true;
    }

    private bool TryGetFanStopSettings(out FanStopSettings settings)
    {
        settings = FanStopSettings.Default;
        double[] values = [FanStopCpuLimit, FanStopGpuLimit, FanStopExitSeconds, AutomaticFanStopCpuLimit, AutomaticFanStopGpuLimit, AutomaticFanStopSeconds];
        if (values.Any(value => !double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > int.MaxValue))
        {
            return false;
        }
        settings = new()
        {
            EndMode = IsCustomFanStopEnd ? FanStopEndMode.Custom : FanStopEndMode.Auto,
            CpuLimitCelsius = (int)FanStopCpuLimit,
            GpuLimitCelsius = (int)FanStopGpuLimit,
            SecondsAboveLimit = (int)FanStopExitSeconds,
            AutomaticEnabled = AutomaticFanStopEnabled,
            AutomaticCpuBelowCelsius = (int)AutomaticFanStopCpuLimit,
            AutomaticGpuBelowCelsius = (int)AutomaticFanStopGpuLimit,
            SecondsBelowLimits = (int)AutomaticFanStopSeconds,
        };
        return FanStopEndModeIndex is 0 or 1 && FanStopSettings.IsWellFormed(settings);
    }

    public async Task ApplyFanStopSettingsAsync(CancellationToken token)
    {
        if (!CanEditFanStopSettings || !TryGetFanStopSettings(out FanStopSettings settings)
            || settings == CurrentFanStopStatus.Settings)
        {
            return;
        }
        IsFanControlOperationInProgress = true;
        NotifyFanStopSettingsPresentation();
        var stamp = new ObservationRequestStamp(connectionGeneration, subscriptionConnected);
        try
        {
            await RunOperationAsync(ct => serviceClient.SetFanStopSettingsAsync(settings, ct), token,
                onCompleted: PresentFanStopOperationResult);
            ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(token);
            await dispatcher.InvokeAsync(() => ApplyObservedStatus(status, stamp, trayOnly: false), token);
        }
        finally
        {
            IsFanControlOperationInProgress = false;
            NotifyFanStopPresentation();
        }
    }

    public async Task ResumeAutomaticFanStopAsync(CancellationToken token)
    {
        if (!CanResumeAutomaticFanStop)
        {
            return;
        }
        IsFanControlOperationInProgress = true;
        NotifyFanStopSettingsPresentation();
        var stamp = new ObservationRequestStamp(connectionGeneration, subscriptionConnected);
        try
        {
            await RunOperationAsync(serviceClient.ResumeAutomaticFanStopAsync, token,
                onCompleted: PresentFanStopOperationResult);
            ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(token);
            await dispatcher.InvokeAsync(() => ApplyObservedStatus(status, stamp, trayOnly: false), token);
        }
        finally
        {
            IsFanControlOperationInProgress = false;
            NotifyFanStopPresentation();
        }
    }

    private void PresentFanStopOperationResult(ControlOperationResult result)
    {
        // Blocked policy, Resume and direct-mode requests do not mean a profile
        // was saved. The shared thermal-operation wording would claim otherwise.
        if (result.Status == ControlOperationStatus.Blocked)
        {
            OperationFeedbackMessage = TrayText.Get(
                "OpenBlade couldn’t complete that action. Check Troubleshooting for details.");
        }
    }

    private void SynchronizeFanStopSettings()
    {
        FanStopSettings settings = CurrentFanStopStatus.Settings;
        // Stable status polling must not discard a partially edited coherent policy.
        if (settings == observedFanStopSettings)
        {
            return;
        }
        synchronizingFanStopSettings = true;
        try
        {
            observedFanStopSettings = settings;
            FanStopEndModeIndex = settings.EndMode == FanStopEndMode.Custom ? 1 : 0;
            FanStopCpuLimit = settings.CpuLimitCelsius;
            FanStopGpuLimit = settings.GpuLimitCelsius;
            FanStopExitSeconds = settings.SecondsAboveLimit;
            AutomaticFanStopEnabled = settings.AutomaticEnabled;
            AutomaticFanStopCpuLimit = settings.AutomaticCpuBelowCelsius;
            AutomaticFanStopGpuLimit = settings.AutomaticGpuBelowCelsius;
            AutomaticFanStopSeconds = settings.SecondsBelowLimits;
        }
        finally
        {
            synchronizingFanStopSettings = false;
        }
    }

    private void NotifyFanStopSettingsPresentation()
    {
        NotifyComputed(nameof(IsCustomFanStopEnd), IsCustomFanStopEnd);
        NotifyComputed(nameof(CanEditFanStopSettings), CanEditFanStopSettings);
        NotifyComputed(nameof(CanResumeAutomaticFanStop), CanResumeAutomaticFanStop);
        NotifyComputed(nameof(FanStopEndSummary), FanStopEndSummary);
        NotifyComputed(nameof(HasUnsavedFanStopSettings), HasUnsavedFanStopSettings);
        NotifyComputed(nameof(AutomaticFanStopSummary), AutomaticFanStopSummary);
        NotifyComputed(nameof(FanStopTemperatureWarning), FanStopTemperatureWarning);
        NotifyComputed(nameof(FanStopDelayWarning), FanStopDelayWarning);
        NotifyComputed(nameof(AutomaticFanStopCpuError), AutomaticFanStopCpuError);
        NotifyComputed(nameof(AutomaticFanStopGpuError), AutomaticFanStopGpuError);
        NotifyComputed(nameof(FanStopSettingsError), FanStopSettingsError);
        NotifyComputed(nameof(HasFanStopTemperatureWarning), HasFanStopTemperatureWarning);
        NotifyComputed(nameof(HasFanStopDelayWarning), HasFanStopDelayWarning);
        NotifyComputed(nameof(HasAutomaticFanStopCpuError), HasAutomaticFanStopCpuError);
        NotifyComputed(nameof(HasAutomaticFanStopGpuError), HasAutomaticFanStopGpuError);
        NotifyComputed(nameof(HasFanStopSettingsError), HasFanStopSettingsError);
    }
}

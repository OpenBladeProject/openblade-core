using System.Diagnostics;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private readonly CoolingPadControlRuntime coolingPadControl;

    public async Task<ControlOperationResult> SetCoolingPadSettingsAsync(CoolingPadSettings settings,
        SecurityIdentifier userSid, Func<bool> stillControlsSession, CancellationToken token)
    {
        try
        {
            await coolingPadControl.SetUserAsync(userSid.Value, token).ConfigureAwait(false);
            long sessionEpoch = Interlocked.Read(ref sessionAdmissionEpoch);
            long powerEpoch = Interlocked.Read(ref coolingPadPowerEpoch);
            bool StillOwns() => CoolingPadSessionContinues(userSid.Value, stillControlsSession);
            bool Allowed() => sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                && powerEpoch == Interlocked.Read(ref coolingPadPowerEpoch)
                && StillOwns() && CoolingPadWriteAdmission(userSid.Value, allowLowPower: false);
            // Epochs admit this finite command. Only a Windows AC/DC event advances the pad's
            // power epoch. Periodic classification runs every 15 s outside the pad's gate and would
            // otherwise cancel any command it overlaps, sometimes after a partial write.
            return await coolingPadControl.ApplyAsync(settings, Allowed, token, continuedOwnership: StillOwns).ConfigureAwait(false);
        }
        finally { PublishCoolingPadStatus(); }
    }

    public async Task<ControlOperationResult> ReadCoolingPadStatusAsync(SecurityIdentifier userSid, CancellationToken token)
    {
        try
        {
            await coolingPadControl.SetUserAsync(userSid.Value, token).ConfigureAwait(false);
            return await coolingPadControl.ReadAsync(token).ConfigureAwait(false);
        }
        finally { PublishCoolingPadStatus(); }
    }

    public async Task<ControlOperationResult> ReleaseCoolingPadControlAsync(SecurityIdentifier userSid,
        Func<bool> stillControlsSession, CancellationToken token)
    {
        try
        {
            await coolingPadControl.SetUserAsync(userSid.Value, token).ConfigureAwait(false);
            return await coolingPadControl.ReleaseAsync(
                () => stillControlsSession() && CoolingPadWriteAdmission(userSid.Value, allowLowPower: false), token,
                serviceOwnedRestoration: true, userRequested: true).ConfigureAwait(false);
        }
        finally { PublishCoolingPadStatus(); }
    }

    public async Task<ControlOperationResult> SetCoolingPadDemandLeaseAsync(
        SetCoolingPadDemandLeaseRequest request, ServiceClientContext client, Func<bool> stillControlsSession, CancellationToken token)
    {
        if (ClientLeaseIdentity(client) is not { } identity)
        { return new(ControlOperationStatus.Blocked, "The cooling pad client process identity is unavailable."); }
        try
        {
            return await coolingPadControl.SetLeaseAsync(request.LeaseId, identity, request.Rpm, request.DurationSeconds,
                () => stillControlsSession() && ClientLeaseIdentity(client) == identity, token).ConfigureAwait(false);
        }
        finally { PublishCoolingPadStatus(); }
    }

    public Task<ControlOperationResult> RemoveCoolingPadDemandLeaseAsync(
        RemoveCoolingPadDemandLeaseRequest request, ServiceClientContext client, CancellationToken token) =>
        ClientLeaseIdentity(client) is { } identity
            ? coolingPadControl.RemoveLeaseAsync(request.LeaseId, identity, token)
            : Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked,
                "The cooling pad client process identity is unavailable."));

    public async Task MaintainCoolingPadAsync(CancellationToken token)
    {
        string? user = Volatile.Read(ref activeUserSid);
        long sessionEpoch = Interlocked.Read(ref sessionAdmissionEpoch);
        long powerEpoch = Interlocked.Read(ref coolingPadPowerEpoch);
        // The pad follows "If settings change outside OpenBlade" for changes found while it is
        // controlled and for resuming control after a handback.
        bool restoreOutsideChanges = CoolingPadRestoresOutsideChanges();
        BatteryAutomationPreferences current = preferences;
        bool restoreOutsideLighting = current.AlwaysOverrideExternalLightingChanges;
        try
        {
            await coolingPadControl.MaintainAsync(
                () => sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                    && powerEpoch == Interlocked.Read(ref coolingPadPowerEpoch)
                    && CoolingPadWriteAdmission(user, allowLowPower: false), token, restoreOutsideChanges,
                restoreOutsideLighting).ConfigureAwait(false);
            if (user is not null && restoreOutsideChanges && !coolingPadControl.Status.Active)
            {
                // Same admission as a user's Apply, bound to the active session. Shutdown
                // has already handed the pad back and must not be undone.
                Func<bool>? controlsSession = activeUserStillControlsSession;
                bool StillOwns() => Volatile.Read(ref fanStopShutdownStarted) == 0
                    && CoolingPadSessionContinues(user, () => controlsSession?.Invoke() == true);
                await coolingPadControl.ResumeSavedControlAsync(user,
                    () => sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                        && powerEpoch == Interlocked.Read(ref coolingPadPowerEpoch)
                        && StillOwns() && CoolingPadWriteAdmission(user, allowLowPower: false),
                    StillOwns, token).ConfigureAwait(false);
            }
        }
        finally { PublishCoolingPadStatus(); }
    }

    // "Restore them" restores; "Notify me" leaves the pad to the change. The pad has no
    // laptop thermal ledger, so the saved choice applies directly. A policy upgraded from
    // an earlier version restores nothing until the user has seen its disclosure, as for
    // the laptop. Lighting additionally needs the lighting ownership opt-in.
    private bool CoolingPadRestoresOutsideChanges()
    {
        BatteryAutomationPreferences current = preferences;
        return current.ThermalControlPolicy == ThermalControlPolicy.MaintainDesiredState
            && current.ThermalPolicyUpgradeDisclosure != ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending;
    }

    private bool CoolingPadSessionContinues(string expectedUser, Func<bool> stillControlsSession)
    {
        try
        {
            return !lowPowerGate.IsActive
                && string.Equals(Volatile.Read(ref activeUserSid), expectedUser, StringComparison.OrdinalIgnoreCase)
                && stillControlsSession();
        }
        catch { return false; }
    }

    private bool CoolingPadPowerAdmission()
    {
        PowerSource fresh = powerStateProvider.CapturePowerSource();
        PowerSource expected = GetStatus().ActivePowerSource;
        // The accessory has its own supply. Confirmed Windows AC presence is sufficient;
        // laptop adapter wattage classification does not change this pad command contract.
        return Enum.IsDefined(fresh) && Enum.IsDefined(expected)
            && (fresh == PowerSource.Battery) == (expected == PowerSource.Battery);
    }

    private bool CoolingPadWriteAdmission(string? expectedUser, bool allowLowPower)
    {
        try
        {
            return (allowLowPower || !lowPowerGate.IsActive)
                && Volatile.Read(ref pendingSessionAdmissions) == 0
                && Volatile.Read(ref pendingPowerAdmissions) == 0
                && !activeUserActivationIncomplete
                && string.Equals(Volatile.Read(ref activeUserSid), expectedUser, StringComparison.OrdinalIgnoreCase)
                && activeUserStillControlsSession?.Invoke() == true
                && CoolingPadPowerAdmission();
            // The typed native pad actor independently performs fresh process/service,
            // exact accessory-HID admission before every SET. Laptop model admission is
            // separate from this accessory. Do not duplicate the
            // full Windows controller/LampArray scan at every software callback here.
        }
        catch { return false; }
    }

    private async Task PrepareCoolingPadForUserTransitionAsync(string previousUser, string newUser,
        Func<bool> stillControlsNewSession, CancellationToken token)
    {
        coolingPadControl.Revoke();
        long sessionEpoch = Interlocked.Read(ref sessionAdmissionEpoch);
        long powerEpoch = Interlocked.Read(ref coolingPadPowerEpoch);
        int ownSessionAdmissions = Volatile.Read(ref pendingSessionAdmissions);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        bool Allowed()
        {
            try
            {
                // This is closed restoration of the service's original protected baseline,
                // not application of either user's preferences. The old user may be logged off.
                return previousUser != newUser && !lowPowerGate.IsActive && !linked.IsCancellationRequested
                    && sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                    && powerEpoch == Interlocked.Read(ref coolingPadPowerEpoch)
                    && Volatile.Read(ref pendingSessionAdmissions) == ownSessionAdmissions
                    && Volatile.Read(ref pendingPowerAdmissions) == 0
                    && string.Equals(Volatile.Read(ref activeUserSid), previousUser, StringComparison.OrdinalIgnoreCase)
                    && stillControlsNewSession() && CoolingPadPowerAdmission();
            }
            catch { return false; }
        }
        try { await coolingPadControl.HandBackForUserTransitionAsync(Allowed, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        { DiagnosticTrace.TraceException(nameof(ServiceControlRuntime), nameof(PrepareCoolingPadForUserTransitionAsync), exception); }
        finally { PublishCoolingPadStatus(); }
    }

    private async Task<ControlOperationResult?> ReleaseCoolingPadForLifecycleAsync(
        bool allowLowPower, CancellationToken token, Func<bool>? stillAuthorized = null)
    {
        if (!coolingPadControl.Status.Active && !coolingPadControl.Status.RecoveryRequired) { return null; }
        coolingPadControl.Revoke();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        try
        {
            return await coolingPadControl.ReleaseAsync(
                () => stillAuthorized?.Invoke() != false
                    && CoolingPadWriteAdmission(Volatile.Read(ref activeUserSid), allowLowPower), linked.Token,
                serviceOwnedRestoration: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(ControlOperationStatus.Failed,
                "Cooling pad cleanup was interrupted; the saved restoration state is retained.");
        }
        catch (Exception exception)
        { return new(ControlOperationStatus.Failed, $"Cooling pad cleanup failed: {exception.Message}"); }
        finally { PublishCoolingPadStatus(); }
    }

    private async Task DrainCoolingPadAndTemperatureSourceAsync()
    {
        try { await coolingPadControl.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception)
        { DiagnosticTrace.TraceException(nameof(ServiceControlRuntime), nameof(DrainCoolingPadAndTemperatureSourceAsync), exception); }
        finally { if (fanTemperatureSource is IDisposable disposable) { disposable.Dispose(); } }
    }

    private void PublishCoolingPadStatus()
    {
        if (GetStatus().CoolingPad == coolingPadControl.Status) { return; }
        // Resolve both snapshots inside the same publication lock. A pad-only update must
        // never roll back an unrelated concurrent profile, message, or confirmed-state update.
        PublishStatusUpdate(current =>
        {
            CoolingPadStatusSnapshot latest = coolingPadControl.Status;
            return current.CoolingPad == latest ? current : current with { CoolingPad = latest };
        });
    }

    private static string? ClientLeaseIdentity(ServiceClientContext client)
    {
        if (client.ProcessId is not > 0 || client.ProcessStartTimeUtcTicks is null) { return null; }
        try
        {
            using Process process = Process.GetProcessById(client.ProcessId.Value);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == client.ProcessStartTimeUtcTicks
                ? $"{client.UserSid.Value}:{client.SessionId}:{client.ProcessId}:{client.ProcessStartTimeUtcTicks}"
                : null;
        }
        catch { return null; }
    }
}

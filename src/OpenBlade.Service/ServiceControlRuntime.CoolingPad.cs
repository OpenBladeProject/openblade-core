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
            long powerEpoch = Interlocked.Read(ref powerAdmissionEpoch);
            bool StillOwns() => CoolingPadSessionContinues(userSid.Value, stillControlsSession);
            bool Allowed() => sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                && powerEpoch == Interlocked.Read(ref powerAdmissionEpoch)
                && StillOwns() && CoolingPadWriteAdmission(userSid.Value, allowLowPower: false);
            // Epochs admit this finite command. Periodic power classification is not loss of
            // the long-lived Windows-session owner and must not stop a healthy pad session.
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
                serviceOwnedRestoration: true).ConfigureAwait(false);
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
        long powerEpoch = Interlocked.Read(ref powerAdmissionEpoch);
        try
        {
            await coolingPadControl.MaintainAsync(
                () => sessionEpoch == Interlocked.Read(ref sessionAdmissionEpoch)
                    && powerEpoch == Interlocked.Read(ref powerAdmissionEpoch)
                    && CoolingPadWriteAdmission(user, allowLowPower: false), token).ConfigureAwait(false);
        }
        finally { PublishCoolingPadStatus(); }
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
            // exact-host and exact-HID admission before every SET. Do not duplicate the
            // full Windows controller/LampArray scan at every software callback here.
        }
        catch { return false; }
    }

    private async Task PrepareCoolingPadForUserTransitionAsync(string previousUser, string newUser,
        Func<bool> stillControlsNewSession, CancellationToken token)
    {
        coolingPadControl.Revoke();
        long sessionEpoch = Interlocked.Read(ref sessionAdmissionEpoch);
        long powerEpoch = Interlocked.Read(ref powerAdmissionEpoch);
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
                    && powerEpoch == Interlocked.Read(ref powerAdmissionEpoch)
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

using OpenBlade.Contracts;
using OpenBlade.Core.Native;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    // The Session Agent observes Windows Dynamic Lighting from the current
    // user's WinRT LampArray view; the service cannot see it as LocalSystem.
    // These fields change only under operationGate and bind the observation to
    // the exact model that was verified when it was accepted.
    private string? observedLampArrayModel;
    private int observedLampArrayOwnership;

    private bool WindowsDynamicLightingOwnsLampArray =>
        ObservedLampArrayOwnership == WindowsDynamicLightingOwnership.Owned
        && ObservationMatchesDevice(Volatile.Read(ref status));

    private bool ObservationMatchesDevice(ServiceStatusSnapshot? snapshot) =>
        snapshot is { DeviceVerified: true }
        && Volatile.Read(ref observedLampArrayModel) is { } model
        && WindowsDynamicLightingLampArrays.Find(snapshot.Firmware?.ModelNumber) is { } profile
        && string.Equals(profile.ModelNumber, model, StringComparison.Ordinal);

    // A device change or a lost verification turns the observation back into
    // Unknown in every published status without discarding it: the same exact
    // laptop returning reports again within one observation interval.
    private WindowsDynamicLightingOwnership PublishedWindowsDynamicLightingOwnership(
        ServiceStatusSnapshot snapshot) =>
        snapshot.Capabilities?.LampArrayLightingAccess is not null
            && ObservationMatchesDevice(snapshot)
            ? ObservedLampArrayOwnership
            : WindowsDynamicLightingOwnership.Unknown;

    private WindowsDynamicLightingOwnership ObservedLampArrayOwnership =>
        (WindowsDynamicLightingOwnership)Volatile.Read(ref observedLampArrayOwnership);

    public async Task<ControlOperationResult> ReportWindowsDynamicLightingOwnershipAsync(
        ReportWindowsDynamicLightingOwnershipRequest request,
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        EnsureInitialized();
        if (request.Ownership is not (WindowsDynamicLightingOwnership.Owned
                or WindowsDynamicLightingOwnership.Released)
            || WindowsDynamicLightingLampArrays.Find(request.ModelNumber) is not { } reported
            || !string.Equals(reported.ModelNumber, request.ModelNumber, StringComparison.Ordinal))
        {
            return RecordRejectedIntent(new ArgumentException(
                "The Windows Dynamic Lighting report does not name a reviewed LampArray keyboard."));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession())
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Windows Dynamic Lighting reports require the active local Windows session.");
            }

            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || current.Capabilities?.LampArrayLightingAccess is null
                || WindowsDynamicLightingLampArrays.Find(current.Firmware?.ModelNumber) is not { } profile
                || !string.Equals(profile.ModelNumber, request.ModelNumber, StringComparison.Ordinal))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The Windows Dynamic Lighting report does not match the verified laptop.");
            }

            bool wasOwned = WindowsDynamicLightingOwnsLampArray;
            bool owned = request.Ownership == WindowsDynamicLightingOwnership.Owned;
            // Record ownership before retiring the renderer so every write path
            // that runs after this point is already blocked.
            Volatile.Write(ref observedLampArrayModel, profile.ModelNumber);
            Volatile.Write(ref observedLampArrayOwnership, (int)request.Ownership);
            if (owned)
            {
                // Retire any live renderer without writing the keyboard and drop
                // pending restores: Windows' state is never journaled or
                // overwritten. Repeated reports reassert the same boundary.
                pendingLightingRestore = null;
                pendingLightingRestoreFromLifecycle = false;
                faultedLightingRecoveryCandidate = null;
                lightingSuppressions.Clear();
                lightingSuppressionWritePending = false;
                // The experimental backend's ordinary close restores the owned
                // keyboard frame, which is a lighting write; its write-free close
                // keeps that frame off the keyboard. The verified RZ09-0528 port
                // writes no lighting on close. It only returns the keyboard
                // Driver mode it owns, as the existing delegation path does, so
                // its Driver-mode accounting holds.
                bool writeFreeClose = deviceProvider.IsExperimentalDevice;
                try
                {
                    await InvalidateLightingRendererCoreAsync(
                        preserveForLifecycleRestore: false,
                        token,
                        lowPower: writeFreeClose).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not (OperationCanceledException or NativeComponentException))
                {
                    string failed =
                        "Windows Dynamic Lighting took over keyboard lighting, but OpenBlade could not confirm that its lighting renderer stopped. OpenBlade will not write keyboard lighting.";
                    PublishIfChanged(GetStatus() with
                    {
                        LightingSuppressions = CurrentLightingSuppressions(),
                        Message = failed,
                    });
                    history.RecordSummary(ControlOperationStatus.Failed, failed);
                    return new ControlOperationResult(ControlOperationStatus.Failed, failed);
                }
            }

            if (wasOwned == owned)
            {
                PublishIfChanged(GetStatus());
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    owned
                        ? "Windows Dynamic Lighting still controls keyboard lighting."
                        : "Windows Dynamic Lighting does not control keyboard lighting.");
            }

            string message = owned
                ? WindowsDynamicLightingOwnsKeyboard().Message
                : "Windows Dynamic Lighting released keyboard lighting. No lighting change was applied.";
            PublishIfChanged(GetStatus() with
            {
                LightingRendererOwnership = owned
                    ? LightingRendererOwnershipState.None
                    : GetStatus().LightingRendererOwnership,
                LightingSuppressions = CurrentLightingSuppressions(),
                Message = message,
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);
            return new ControlOperationResult(ControlOperationStatus.Stored, message);
        }
        finally
        {
            operationGate.Release();
        }
    }
}

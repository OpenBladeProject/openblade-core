using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    public ThxAudioProfileSettings? ActiveSavedThxAudioProfile =>
        ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            ? GetProfile(ActivePowerSource).ThxAudio : null;

    public bool CanSaveThxAudioProfile => CanMutate
        && Capabilities.ThxAudio is { Access: DeviceFeatureAccess.ReadWrite, ProductId: ThxAudioProductIds.Rznb11 or ThxAudioProductIds.Rznb12 }
        && ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;

    public async Task<bool> SaveThxAudioProfileAsync(
        ThxAudioProfileSettings settings,
        PowerSource expectedSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.Token);
        cancellationToken = persistenceCancellation.Token;
        if (!await BeginProfilePersistenceAsync(cancellationToken))
        {
            return false;
        }
        try
        {
            if (!CanSaveThxAudioProfile || ActivePowerSource != expectedSource)
            {
                return false;
            }

            PowerProfile updated = GetProfile(expectedSource) with { ThxAudio = settings };
            ProfileSet candidate = expectedSource switch
            {
                PowerSource.PluggedIn => Profiles with { PluggedIn = updated },
                PowerSource.Battery => Profiles with { Battery = updated },
                PowerSource.UsbC => Profiles with { UsbC = updated },
                _ => throw new ArgumentOutOfRangeException(nameof(expectedSource)),
            };
            if (!TryGetSupportedProfileDraft(candidate, out ProfileSet validated))
            {
                return false;
            }
            // Other unsaved editors retain their owners; only confirmed persistence
            // replaces the saved profile document.
            return await PersistProfileSetAsync(validated, cancellationToken);
        }
        finally
        {
            profilePersistenceGate.Release();
        }
    }
}

using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    public ThxAudioProfileSettings? ActiveSavedThxAudioProfile =>
        ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            ? GetProfile(ActivePowerSource).ThxAudio : null;

    public bool CanSaveThxAudioProfile => CanMutate
        && Capabilities.ThxAudio is { Access: DeviceFeatureAccess.ReadWrite, ProductId: ThxAudioProductIds.Rznb12 }
        && ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;

    public async Task<bool> SaveThxAudioProfileAsync(
        ThxAudioProfileSettings settings,
        PowerSource expectedSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
        bool accepted = false;
        await RunOperationAsync(
            token => serviceClient.UpdateProfilesAsync(validated, token), cancellationToken,
            result =>
            {
                if (result.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied)
                {
                    // Audio saves replace only this profile's audio settings.
                    // Other unsaved editors retain their owners. A blocked save
                    // must not replace the last confirmed local profile.
                    Profiles = validated;
                    accepted = true;
                }
            });
        return accepted;
    }
}

using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    public IReadOnlyList<ApplicationProfileDefinition> ApplicationProfiles => Profiles.ApplicationProfiles;

    public string ActiveApplicationProfileName => latestStatus?.ActiveApplicationProfileId is { } id
        ? Profiles.ApplicationProfiles.FirstOrDefault(profile => profile.Id == id)?.Name
            ?? TrayText.Get("Unknown application")
        : TrayText.Get("Power-source profile");

    public async Task<bool> SaveApplicationProfileAsync(
        ApplicationProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ApplicationProfileDefinition[] updated = Profiles.ApplicationProfiles
            .Where(item => item.Id != profile.Id).ToArray();
        int existingIndex = Profiles.ApplicationProfiles.ToList().FindIndex(item => item.Id == profile.Id);
        List<ApplicationProfileDefinition> ordered = [.. updated];
        ordered.Insert(existingIndex < 0 ? ordered.Count : existingIndex, profile);
        return await SaveApplicationProfilesAsync(ordered, cancellationToken);
    }

    public Task<bool> RemoveApplicationProfileAsync(Guid id, CancellationToken cancellationToken) =>
        SaveApplicationProfilesAsync(Profiles.ApplicationProfiles.Where(item => item.Id != id).ToArray(),
            cancellationToken);

    private async Task<bool> SaveApplicationProfilesAsync(
        IReadOnlyList<ApplicationProfileDefinition> applicationProfiles,
        CancellationToken cancellationToken)
    {
        if (!CanMutate)
        {
            return false;
        }
        ProfileSet candidate = Profiles with { ApplicationProfiles = applicationProfiles };
        bool persisted = false;
        await RunOperationAsync(
            token => serviceClient.UpdateProfilesAsync(candidate, token),
            cancellationToken,
            result => persisted = result.Status is
                ControlOperationStatus.Stored or ControlOperationStatus.Applied);
        if (persisted)
        {
            Profiles = candidate;
        }
        return persisted;
    }
}

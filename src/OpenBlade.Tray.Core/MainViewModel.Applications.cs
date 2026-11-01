using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    public IReadOnlyList<ApplicationProfileDefinition> ApplicationProfiles => Profiles.ApplicationProfiles;

    public string ActiveApplicationProfileName => latestStatus?.ActiveApplicationProfileId is { } id
        ? Profiles.ApplicationProfiles.FirstOrDefault(profile => profile.Id == id)?.Name
            ?? TrayText.Get("Unknown application")
        : TrayText.Get("Power-source profile");

    public Task<bool> SaveApplicationProfileAsync(
        ApplicationProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SaveApplicationProfilesAsync(profiles =>
        {
            ApplicationProfileDefinition[] updated = profiles.ApplicationProfiles
                .Where(item => item.Id != profile.Id).ToArray();
            int existingIndex = profiles.ApplicationProfiles.ToList().FindIndex(item => item.Id == profile.Id);
            List<ApplicationProfileDefinition> ordered = [.. updated];
            ordered.Insert(existingIndex < 0 ? ordered.Count : existingIndex, profile);
            return profiles with { ApplicationProfiles = ordered };
        }, cancellationToken);
    }

    public Task<bool> SetApplicationProfileEnabledAsync(
        Guid id, bool enabled, CancellationToken cancellationToken) =>
        SaveApplicationProfilesAsync(profiles => profiles with
        {
            ApplicationProfiles = profiles.ApplicationProfiles
                .Select(profile => profile.Id == id ? profile with { Enabled = enabled } : profile).ToArray(),
        }, cancellationToken);

    public Task<bool> RemoveApplicationProfileAsync(Guid id, CancellationToken cancellationToken) =>
        SaveApplicationProfilesAsync(profiles => profiles with
        {
            ApplicationProfiles = profiles.ApplicationProfiles.Where(item => item.Id != id).ToArray(),
        }, cancellationToken);

    private async Task<bool> SaveApplicationProfilesAsync(
        Func<ProfileSet, ProfileSet> update,
        CancellationToken cancellationToken)
    {
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.Token);
        cancellationToken = persistenceCancellation.Token;
        if (!await BeginProfilePersistenceAsync(cancellationToken))
        {
            return false;
        }
        try
        {
            if (!CanMutate)
            {
                return false;
            }
            ProfileSet candidate = update(Profiles);
            return candidate == Profiles || await PersistProfileSetAsync(candidate, cancellationToken);
        }
        finally
        {
            profilePersistenceGate.Release();
        }
    }
}

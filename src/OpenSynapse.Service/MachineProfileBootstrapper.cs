namespace OpenSynapse.Service;

public sealed class MachineProfileBootstrapper(ProfileStore store)
{
    public async Task<ProfileLoadResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ProfileLoadResult result = await store.LoadAsync(cancellationToken);
        await store.SaveAsync(result.Profiles, cancellationToken);
        return result;
    }
}

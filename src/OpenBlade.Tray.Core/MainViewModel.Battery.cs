using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    public async Task<bool> SaveLongBatteryLifePresetAsync(
        LongBatteryLifePresetPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!CanMutate || Profiles.Battery != plan.Original)
        {
            return false;
        }
        ProfileSet candidate = Profiles with { Battery = plan.Candidate };
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

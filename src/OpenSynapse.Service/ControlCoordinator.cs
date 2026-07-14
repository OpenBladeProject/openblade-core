using OpenSynapse.Contracts;
using OpenSynapse.Protocol;

namespace OpenSynapse.Service;

public interface IConflictDetector
{
    IReadOnlyList<string> GetRunningConflicts();
}

public sealed class ControlCoordinator(
    IBladeDevice device,
    IConflictDetector conflictDetector)
{
    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> conflicts = conflictDetector.GetRunningConflicts();
        if (conflicts.Count != 0)
        {
            return new ControlResult(
                ControlResultStatus.Blocked,
                $"Hardware writes are blocked while these controllers are running: {string.Join(", ", conflicts)}");
        }

        ConfirmedDeviceState state = await device.ReadStateAsync(cancellationToken);
        if (state.Performance == value)
        {
            return new ControlResult(ControlResultStatus.Unchanged, "Performance is already confirmed.");
        }

        await device.SetPerformanceAsync(value, cancellationToken);
        return new ControlResult(ControlResultStatus.Applied, "Performance setting applied.");
    }
}

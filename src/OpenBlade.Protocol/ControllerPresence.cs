using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public sealed record ControllerPresenceObservation(
    IReadOnlyList<string> PotentialControllers,
    string? DetectionWarning);

public interface IControllerPresenceMonitor
{
    ControllerPresenceObservation Observe();
}

internal enum ControllerExclusivityPolicy
{
    Strict,
    Advisory,
}

public sealed class WindowsControllerPresenceMonitor : IControllerPresenceMonitor
{
    private readonly IRunningProcessSource processSource;
    private readonly IWindowsServiceSource serviceSource;
    private readonly Func<IReadOnlyList<RunningControllerProcess>>? processSnapshots;
    private readonly SynapseBladeBlockerVerifier blocker = new();

    public WindowsControllerPresenceMonitor(
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource)
        : this(processSource, serviceSource,
            processSource is RunningProcessSource live ? live.GetControllerProcessSnapshots : null)
    {
    }

    internal WindowsControllerPresenceMonitor(
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        Func<IReadOnlyList<RunningControllerProcess>>? processSnapshots)
    {
        this.processSource = processSource;
        this.serviceSource = serviceSource;
        this.processSnapshots = processSnapshots;
    }

    private static readonly HashSet<string> KnownControllerProcesses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "RazerAppEngine",
        "razerwdl",
    };

    public ControllerPresenceObservation Observe()
    {
        List<string> potentialControllers = [];
        bool processFailed = false;
        bool serviceFailed = false;
        bool patchedSynapse = false;

        try
        {
            IReadOnlyList<RunningControllerProcess>? snapshots = processSnapshots?.Invoke();
            IReadOnlyList<string> names = snapshots is null
                ? processSource.GetProcessNames()
                : snapshots.Select(process => process.Name).ToArray();
            RunningControllerProcess[] engines = snapshots?
                .Where(process => process.Name.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            // Shared Razer services and razerwdl remain active for peripherals.
            // Only fully verified live Synapse engines can waive these aliases.
            patchedSynapse = engines.Length > 0 && engines.All(blocker.IsApplied);
            potentialControllers.AddRange(names
                .Where(process =>
                    KnownControllerProcesses.Contains(process)
                    || RazerControllerServiceAliases.Contains(process))
                .Where(_ => !patchedSynapse));
        }
        catch (Exception)
        {
            processFailed = true;
        }

        try
        {
            potentialControllers.AddRange(serviceSource
                .GetServices()
                .Where(service => IsActive(service.State) && !patchedSynapse)
                .Where(service => RazerControllerServiceAliases.MatchesService(
                    service.ServiceName,
                    service.DisplayName))
                .Select(service => RazerControllerServiceAliases.Contains(service.DisplayName)
                    ? service.DisplayName
                    : service.ServiceName));
        }
        catch (Exception)
        {
            serviceFailed = true;
        }

        string[] confirmed = potentialControllers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(conflict => conflict, StringComparer.Ordinal)
            .ToArray();
        string? detectionWarning = (processFailed, serviceFailed) switch
        {
            (true, true) => "Process and Windows service enumeration failed.",
            (true, false) => "Process enumeration failed.",
            (false, true) => "Windows service enumeration failed.",
            _ => null,
        };

        return new ControllerPresenceObservation(confirmed, detectionWarning);
    }

    private static bool IsActive(WindowsServiceState state) =>
        state is WindowsServiceState.Running
            or WindowsServiceState.StartPending
            or WindowsServiceState.ContinuePending;
}

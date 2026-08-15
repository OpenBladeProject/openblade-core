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

public sealed class WindowsControllerPresenceMonitor(
    IRunningProcessSource processSource,
    IWindowsServiceSource serviceSource) : IControllerPresenceMonitor
{
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

        try
        {
            potentialControllers.AddRange(processSource
                .GetProcessNames()
                .Where(process =>
                    KnownControllerProcesses.Contains(process)
                    || RazerControllerServiceAliases.Contains(process)));
        }
        catch (Exception)
        {
            processFailed = true;
        }

        try
        {
            potentialControllers.AddRange(serviceSource
                .GetServices()
                .Where(service => IsActive(service.State))
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

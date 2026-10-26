using System.ServiceProcess;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal sealed record RazerServiceSnapshot(
    string ServiceName,
    string DisplayName,
    ServiceControllerStatus Status,
    bool CanStop);

internal sealed record RazerServiceInventory(
    IReadOnlyList<RazerServiceSnapshot> Services,
    string? Error)
{
    public bool Complete => Error is null;
}

internal sealed record RazerServiceStopFailure(
    string ServiceName,
    string DisplayName,
    string Message);

internal enum RazerServiceStopResult
{
    Stopped,
    NotStoppable,
    TimedOut,
}

internal sealed record RazerServiceStopExecutionResult(
    IReadOnlyList<RazerServiceSnapshot> Candidates,
    IReadOnlyList<RazerServiceStopFailure> Failures,
    string? InventoryError)
{
    public bool Succeeded => InventoryError is null && Failures.Count == 0;
}

internal sealed record RazerServiceStopReadback(
    IReadOnlyList<string> StoppedOrRemoved,
    IReadOnlyList<RazerServiceSnapshot> RemainingActive);

internal static class RazerServiceStopOperation
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    internal const int MaximumPolls = 15;

    internal static Task<RazerServiceInventory> ObserveAsync(
        CancellationToken cancellationToken) =>
        Task.Run(ReadInventory, cancellationToken);

    internal static RazerServiceInventory ReadInventory()
    {
        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.RazerServiceStopOperation",
                "EnumerateServices",
                exception);
            return new([], $"Windows service enumeration failed: {exception.Message}");
        }

        try
        {
            var snapshots = new List<RazerServiceSnapshot>();
            foreach (ServiceController service in services)
            {
                try
                {
                    string serviceName = service.ServiceName;
                    string displayName = service.DisplayName;
                    if (!IsKnownControllerService(serviceName, displayName))
                    {
                        continue;
                    }

                    service.Refresh();
                    snapshots.Add(new(
                        serviceName,
                        displayName,
                        service.Status,
                        service.CanStop));
                }
                catch (Exception exception)
                {
                    DiagnosticTrace.TraceException(
                        "OpenBlade.Tray.RazerServiceStopOperation",
                        "InspectService",
                        exception);
                    return new([], $"A matched Razer service could not be inspected: {exception.Message}");
                }
            }

            return CreateInventory(snapshots);
        }
        finally
        {
            foreach (ServiceController service in services)
            {
                service.Dispose();
            }
        }
    }

    internal static RazerServiceInventory CreateInventory(
        IEnumerable<RazerServiceSnapshot> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RazerServiceSnapshot[] matches = services
            .Where(service => IsKnownControllerService(
                service.ServiceName,
                service.DisplayName))
            .GroupBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(service => service.DisplayName, StringComparer.Ordinal)
            .ThenBy(service => service.ServiceName, StringComparer.Ordinal)
            .ToArray();
        return new(matches, null);
    }

    internal static IReadOnlyList<RazerServiceSnapshot> ActiveServices(
        RazerServiceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory.Services
            .Where(service => service.Status != ServiceControllerStatus.Stopped)
            .ToArray();
    }

    internal static RazerServiceStopReadback EvaluateReadback(
        IReadOnlyList<RazerServiceSnapshot> initialCandidates,
        RazerServiceInventory finalInventory)
    {
        ArgumentNullException.ThrowIfNull(initialCandidates);
        ArgumentNullException.ThrowIfNull(finalInventory);
        if (!finalInventory.Complete)
        {
            throw new InvalidOperationException(
                "Final Razer service inventory must be complete before classification.");
        }

        Dictionary<string, RazerServiceSnapshot> finalByName = finalInventory.Services
            .ToDictionary(service => service.ServiceName, StringComparer.OrdinalIgnoreCase);
        string[] stoppedOrRemoved = initialCandidates
            .Where(candidate =>
                !finalByName.TryGetValue(candidate.ServiceName, out RazerServiceSnapshot? final)
                || final.Status == ServiceControllerStatus.Stopped)
            .Select(candidate => candidate.DisplayName)
            .ToArray();
        return new(stoppedOrRemoved, ActiveServices(finalInventory));
    }

    internal static async Task<RazerServiceStopExecutionResult> StopAllAsync(
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
                ReadInventory,
                StopWindowsServiceAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<RazerServiceStopExecutionResult> ExecuteAsync(
        Func<RazerServiceInventory> readInventory,
        Func<string, CancellationToken, Task<RazerServiceStopResult>> stopAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readInventory);
        ArgumentNullException.ThrowIfNull(stopAsync);
        cancellationToken.ThrowIfCancellationRequested();

        RazerServiceInventory inventory = readInventory();
        if (!inventory.Complete)
        {
            return new([], [], inventory.Error);
        }

        IReadOnlyList<RazerServiceSnapshot> candidates = ActiveServices(inventory);
        var failures = new List<RazerServiceStopFailure>();
        foreach (RazerServiceSnapshot candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RazerServiceStopResult result = await stopAsync(
                        candidate.ServiceName,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result != RazerServiceStopResult.Stopped)
                {
                    failures.Add(new(
                        candidate.ServiceName,
                        candidate.DisplayName,
                        result == RazerServiceStopResult.NotStoppable
                            ? "The service does not currently accept stop control."
                            : "The service did not reach Stopped before the bounded wait ended."));
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.RazerServiceStopOperation",
                    "StopService",
                    exception);
                failures.Add(new(
                    candidate.ServiceName,
                    candidate.DisplayName,
                    exception.Message));
            }
        }

        return new(candidates, failures, null);
    }

    internal static async Task<RazerServiceStopResult> StopOneAsync(
        Func<ServiceControllerStatus> readStatus,
        Func<bool> readCanStop,
        Action stop,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        int maximumPolls,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(readCanStop);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (maximumPolls <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPolls));
        }
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        bool stopRequested = false;
        for (int poll = 0; poll < maximumPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceControllerStatus status = readStatus();
            if (status == ServiceControllerStatus.Stopped)
            {
                return RazerServiceStopResult.Stopped;
            }

            if (!stopRequested
                && status is ServiceControllerStatus.Running
                    or ServiceControllerStatus.Paused)
            {
                if (!readCanStop())
                {
                    return RazerServiceStopResult.NotStoppable;
                }

                cancellationToken.ThrowIfCancellationRequested();
                stop();
                stopRequested = true;
            }
            else if (!stopRequested
                && status is not ServiceControllerStatus.StartPending
                    and not ServiceControllerStatus.ContinuePending
                    and not ServiceControllerStatus.PausePending
                    and not ServiceControllerStatus.StopPending)
            {
                return RazerServiceStopResult.NotStoppable;
            }

            if (poll + 1 < maximumPolls)
            {
                await delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return RazerServiceStopResult.TimedOut;
    }

    private static async Task<RazerServiceStopResult> StopWindowsServiceAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        using var service = new ServiceController(serviceName);
        return await StopOneAsync(
                () =>
                {
                    service.Refresh();
                    return service.Status;
                },
                () =>
                {
                    service.Refresh();
                    return service.CanStop;
                },
                () => service.Stop(stopDependentServices: false),
                Task.Delay,
                MaximumPolls,
                PollInterval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsKnownControllerService(
        string serviceName,
        string displayName) =>
        RazerControllerServiceAliases.MatchesService(serviceName, displayName);
}

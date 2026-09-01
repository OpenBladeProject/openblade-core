using OpenBlade.Platform;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionGpuRequestDispatcher
{
    private readonly IWindowsGpuAppManager manager;

    public SessionGpuRequestDispatcher(IWindowsGpuAppManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public Task<SessionGpuResponse> DispatchAsync(
        SessionGpuRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Dispatch(request, cancellationToken), cancellationToken);
    }

    private SessionGpuResponse Dispatch(
        SessionGpuRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Version != SessionGpuIpc.ProtocolVersion)
        {
            return Response(
                request.RequestId,
                SessionGpuOperationStatus.Failed,
                "The Session GPU IPC protocol version is unsupported.",
                [],
                []);
        }
        if (request is CloseGpuAppsRequest { Force: true, Targets.Count: not 1 })
        {
            return Response(
                request.RequestId,
                SessionGpuOperationStatus.Failed,
                "Force close requires exactly one confirmed application.",
                [],
                []);
        }

        WindowsGpuAppScan scan = manager.Scan(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!scan.IsAvailable)
        {
            return Response(
                request.RequestId,
                SessionGpuOperationStatus.Failed,
                scan.Error ?? "GPU activity is unavailable.",
                [],
                []);
        }
        if (scan.Apps.Count > SessionGpuIpc.MaximumApps)
        {
            return Response(
                request.RequestId,
                SessionGpuOperationStatus.Failed,
                "Too many GPU applications were detected to return safely.",
                [],
                []);
        }

        if (request is QueryGpuAppsRequest)
        {
            return Response(
                request.RequestId,
                SessionGpuOperationStatus.Succeeded,
                null,
                scan.Apps,
                []);
        }

        var close = (CloseGpuAppsRequest)request;
        Dictionary<int, WindowsGpuApp> currentById = scan.Apps.ToDictionary(app => app.ProcessId);
        List<WindowsGpuApp> matched = [];
        List<SessionGpuActionResult> stale = [];
        foreach (SessionGpuAppIdentity target in close.Targets)
        {
            if (currentById.TryGetValue(target.ProcessId, out WindowsGpuApp? current)
                && current.StartTimeUtcTicks == target.StartTimeUtcTicks
                && string.Equals(current.Name, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(current);
            }
            else
            {
                stale.Add(new SessionGpuActionResult(
                    target.ProcessId,
                    target.StartTimeUtcTicks,
                    false,
                    "The process no longer holds GPU resources or its identity changed after confirmation."));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WindowsGpuAppActionResult> action = matched.Count == 0
            ? []
            : close.Force
                ? manager.ForceClose(matched, cancellationToken)
                : manager.CloseGracefully(matched, cancellationToken);
        HashSet<(int ProcessId, long StartTimeUtcTicks)> attempted = action
            .Select(result => (result.ProcessId, result.StartTimeUtcTicks))
            .ToHashSet();
        SessionGpuActionResult[] unattempted = cancellationToken.IsCancellationRequested
            ? matched
                .Where(app => !attempted.Contains((app.ProcessId, app.StartTimeUtcTicks)))
                .Select(app => new SessionGpuActionResult(
                    app.ProcessId,
                    app.StartTimeUtcTicks,
                    false,
                    "This process was not attempted because the GPU app operation stopped."))
                .ToArray()
            : [];
        bool interrupted = unattempted.Length > 0;
        SessionGpuActionResult[] results = stale
            .Concat(action.Select(Map))
            .Concat(unattempted)
            .ToArray();
        int succeeded = results.Count(result => result.Succeeded);
        SessionGpuOperationStatus status = succeeded == results.Length
            ? SessionGpuOperationStatus.Succeeded
            : succeeded == 0
                ? SessionGpuOperationStatus.Failed
                : SessionGpuOperationStatus.PartiallySucceeded;
        string? error = interrupted
            ? "The GPU app operation stopped before all selected applications were processed."
            : status switch
            {
                SessionGpuOperationStatus.Succeeded => null,
                SessionGpuOperationStatus.PartiallySucceeded =>
                    "Some applications could not be closed.",
                _ => "The selected applications could not be closed.",
            };
        return Response(request.RequestId, status, error, scan.Apps, results);
    }

    private static SessionGpuResponse Response(
        Guid requestId,
        SessionGpuOperationStatus status,
        string? error,
        IReadOnlyList<WindowsGpuApp> apps,
        IReadOnlyList<SessionGpuActionResult> results) => new(
            SessionGpuIpc.ProtocolVersion,
            requestId,
            status,
            error,
            apps.Take(SessionGpuIpc.MaximumApps).Select(Map).ToArray(),
            results.Take(SessionGpuIpc.MaximumApps).ToArray());

    private static SessionGpuApp Map(WindowsGpuApp app) => new(
        app.ProcessId,
        app.StartTimeUtcTicks,
        app.Name,
        app.ExecutablePath,
        app.UtilizationPercent,
        app.CanClose,
        app.BlockReason);

    private static SessionGpuActionResult Map(WindowsGpuAppActionResult result) => new(
        result.ProcessId,
        result.StartTimeUtcTicks,
        result.Succeeded,
        result.Error);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed class ApplicationProfileReporter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLeaseRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Func<ReportApplicationContextRequest, CancellationToken,
        Task<ControlOperationResult>> report;
    private readonly object statusSync = new();
    private readonly Channel<Event> events = Channel.CreateBounded<Event>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Guid instanceId = Guid.NewGuid();
    private readonly Func<int, IApplicationProcessHandle> openProcess;
    private readonly TimeSpan leaseRefreshInterval;
    private readonly Dictionary<int, TrackedProcess> tracked = [];
    private IReadOnlyList<ApplicationProfileDefinition> profiles = [];
    private IReadOnlyList<ApplicationProfileDefinition> pendingProfiles = [];
    private ThxForegroundObservation foreground = ThxForegroundObservation.Unknown;
    private string? reportedPath;
    private bool reportPending;
    private bool statusPending;
    private long sequence;
    private long activationSequence;
    private int disposed;

    internal ApplicationProfileReporter(
        Func<ReportApplicationContextRequest, CancellationToken,
            Task<ControlOperationResult>> report,
        Func<int, IApplicationProcessHandle>? openProcess = null,
        TimeSpan? leaseRefreshInterval = null)
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        this.openProcess = openProcess ?? WindowsApplicationProcessHandle.Open;
        this.leaseRefreshInterval = leaseRefreshInterval ?? DefaultLeaseRefreshInterval;
        if (this.leaseRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseRefreshInterval));
        }
    }

    internal void Observe(ServiceStatusSnapshot status)
    {
        lock (statusSync)
        {
            pendingProfiles = status.Profiles.ApplicationProfiles;
            statusPending = true;
        }
        events.Writer.TryWrite(StatusObserved.Instance);
    }

    internal void ObserveForeground(ThxForegroundObservation observation) =>
        events.Writer.TryWrite(new ForegroundObserved(observation));

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(leaseRefreshInterval);
        Task<bool> tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
        Task<bool> read = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
        while (await Task.WhenAny(tick, read).ConfigureAwait(false) is { } completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == tick)
            {
                if (await tick.ConfigureAwait(false))
                {
                    RemoveExitedProcesses();
                    bool reconciled = await ReconcileAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!reconciled && (reportedPath is not null || reportPending))
                    {
                        await SendAsync(reportedPath, cancellationToken).ConfigureAwait(false);
                    }
                }
                tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            }
            if (completed == read)
            {
                if (!await read.ConfigureAwait(false))
                {
                    return;
                }
                ApplyLatestStatus();
                while (events.Reader.TryRead(out Event? item))
                {
                    Process(item);
                }
                ApplyLatestStatus();
                _ = await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                read = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            events.Writer.TryComplete();
            foreach (TrackedProcess value in tracked.Values)
            {
                value.Process.Dispose();
            }
            tracked.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private void Process(Event item)
    {
        switch (item)
        {
            case StatusObserved:
                ApplyLatestStatus();
                break;
            case ForegroundObserved observed:
                foreground = observed.Observation;
                TrackWhileRunningForeground();
                break;
            case ProcessExited exited:
                if (tracked.TryGetValue(exited.ProcessId, out TrackedProcess? process)
                    && process.ActivationSequence == exited.ActivationSequence
                    && tracked.Remove(exited.ProcessId))
                {
                    process.Process.Dispose();
                }
                if (foreground.ProcessId == exited.ProcessId
                    && process?.ActivationSequence == exited.ActivationSequence)
                {
                    foreground = ThxForegroundObservation.Unknown;
                }
                break;
        }
    }

    private void ApplyLatestStatus()
    {
        IReadOnlyList<ApplicationProfileDefinition> latest;
        lock (statusSync)
        {
            if (!statusPending)
            {
                return;
            }
            latest = pendingProfiles;
            statusPending = false;
        }
        profiles = latest;
        RemoveInvalidTrackedProcesses();
        TrackWhileRunningForeground();
    }

    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        ApplyLatestStatus();
        string? next = SelectActivePath();
        if (!string.Equals(next, reportedPath, StringComparison.OrdinalIgnoreCase))
        {
            reportedPath = next;
            reportPending = true;
            await SendAsync(next, cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task SendAsync(string? executablePath, CancellationToken cancellationToken)
    {
        try
        {
            ControlOperationResult result = await report(
                new ReportApplicationContextRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    instanceId,
                    checked(++sequence),
                    executablePath),
                cancellationToken).ConfigureAwait(false);
            if (result.Status == ControlOperationStatus.Blocked)
            {
                Trace.TraceWarning("Application profile report was blocked: {0}", result.Message);
            }
            reportPending = result.Status is not (
                ControlOperationStatus.Stored or ControlOperationStatus.Applied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Application profile report failed: {0}", exception.Message);
        }
    }

    private string? SelectActivePath()
    {
        return SelectActivePath(
            profiles,
            foreground.ExecutablePath,
            tracked.Values.Select(value => (
                value.ProfileId,
                value.ExecutablePath,
                value.ActivationSequence)));
    }

    internal static string? SelectActivePath(
        IReadOnlyList<ApplicationProfileDefinition> profiles,
        string? foregroundPath,
        IEnumerable<(Guid ProfileId, string ExecutablePath, long ActivationSequence)> activated)
    {
        ApplicationProfileDefinition? foregroundProfile = Match(profiles, foregroundPath);
        if (foregroundProfile is not null)
        {
            return foregroundProfile.ExecutablePath;
        }

        return activated
            .Where(value => profiles.Any(profile => profile.Enabled
                && profile.Id == value.ProfileId
                && profile.ActivationMode == ApplicationActivationMode.WhileRunningAfterActivation
                && string.Equals(profile.ExecutablePath, value.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(value => value.ActivationSequence)
            .Select(value => value.ExecutablePath)
            .FirstOrDefault();
    }

    private void TrackWhileRunningForeground()
    {
        ApplicationProfileDefinition? profile = Match(foreground.ExecutablePath);
        if (profile?.ActivationMode != ApplicationActivationMode.WhileRunningAfterActivation
            || foreground.ProcessId <= 0)
        {
            return;
        }
        if (tracked.TryGetValue(foreground.ProcessId, out TrackedProcess? current)
            && current.ProfileId == profile.Id
            && string.Equals(
                current.ExecutablePath,
                profile.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        IApplicationProcessHandle? process = null;
        try
        {
            int processId = foreground.ProcessId;
            process = openProcess(processId);
            string currentPath = ApplicationProfileDefinition.NormalizeExecutablePath(
                process.ExecutablePath ?? string.Empty);
            if (!string.Equals(currentPath, profile.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return;
            }
            if (tracked.Remove(processId, out TrackedProcess? replaced))
            {
                replaced.Process.Dispose();
            }
            long currentActivationSequence = checked(++activationSequence);
            process.Exited += (_, _) => events.Writer.TryWrite(
                new ProcessExited(processId, currentActivationSequence));
            var trackedProcess = new TrackedProcess(
                profile.Id,
                profile.ExecutablePath,
                currentActivationSequence,
                process);
            tracked.Add(processId, trackedProcess);
            if (process.HasExited)
            {
                events.Writer.TryWrite(new ProcessExited(
                    processId,
                    currentActivationSequence));
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
            or Win32Exception)
        {
            process?.Dispose();
            // The process exited between foreground resolution and subscription.
        }
    }

    private ApplicationProfileDefinition? Match(string? path) =>
        Match(profiles, path);

    private static ApplicationProfileDefinition? Match(
        IReadOnlyList<ApplicationProfileDefinition> profiles,
        string? path) => path is null ? null : profiles.FirstOrDefault(profile => profile.Enabled
            && string.Equals(profile.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));

    private void RemoveInvalidTrackedProcesses()
    {
        foreach (int processId in tracked
            .Where(pair => !profiles.Any(profile => profile.Enabled
                && profile.Id == pair.Value.ProfileId
                && profile.ActivationMode == ApplicationActivationMode.WhileRunningAfterActivation
                && string.Equals(profile.ExecutablePath, pair.Value.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key)
            .ToArray())
        {
            tracked.Remove(processId, out TrackedProcess? removed);
            removed?.Process.Dispose();
        }
    }

    private void RemoveExitedProcesses()
    {
        foreach ((int processId, TrackedProcess trackedProcess) in tracked.ToArray())
        {
            bool exited;
            try
            {
                exited = trackedProcess.Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                exited = true;
            }
            if (!exited
                || !tracked.TryGetValue(processId, out TrackedProcess? current)
                || current.ActivationSequence != trackedProcess.ActivationSequence
                || !tracked.Remove(processId))
            {
                continue;
            }
            trackedProcess.Process.Dispose();
            if (foreground.ProcessId == processId)
            {
                foreground = ThxForegroundObservation.Unknown;
            }
        }
    }

    private abstract record Event;
    private sealed record StatusObserved : Event
    {
        internal static readonly StatusObserved Instance = new();
    }
    private sealed record ForegroundObserved(ThxForegroundObservation Observation) : Event;
    private sealed record ProcessExited(int ProcessId, long ActivationSequence) : Event;
    private sealed record TrackedProcess(
        Guid ProfileId,
        string ExecutablePath,
        long ActivationSequence,
        IApplicationProcessHandle Process);
}

internal interface IApplicationProcessHandle : IDisposable
{
    string? ExecutablePath { get; }
    bool HasExited { get; }
    event EventHandler? Exited;
}

internal sealed class WindowsApplicationProcessHandle : IApplicationProcessHandle
{
    private readonly Process process;

    private WindowsApplicationProcessHandle(Process process)
    {
        this.process = process;
        process.EnableRaisingEvents = true;
    }

    public string? ExecutablePath => process.MainModule?.FileName;
    public bool HasExited => process.HasExited;
    public event EventHandler? Exited
    {
        add => process.Exited += value;
        remove => process.Exited -= value;
    }

    public void Dispose() => process.Dispose();

    internal static IApplicationProcessHandle Open(int processId) =>
        new WindowsApplicationProcessHandle(Process.GetProcessById(processId));
}

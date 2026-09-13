using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IUserIdleTimeSource
{
    TimeSpan? GetIdleTime();
}

public sealed class WindowsUserIdleTimeSource : IUserIdleTimeSource
{
    public TimeSpan? GetIdleTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var input = new LastInputInfo
        {
            Size = checked((uint)Marshal.SizeOf<LastInputInfo>()),
        };
        if (!GetLastInputInfo(ref input))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        uint current = unchecked((uint)Environment.TickCount64);
        uint elapsedMilliseconds = unchecked(current - input.TickCount);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint TickCount;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
}

/// <summary>
/// Evaluates display-off and idle-lighting rules in the signed-in user session,
/// where both display status and GetLastInputInfo reflect the interactive
/// desktop. Only typed suppression reasons cross the service boundary.
/// </summary>
public sealed class ServiceLightingIdlePolicyLoop
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnavailableIdleRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IUserIdleTimeSource idleTimeSource;
    private readonly IUserDisplayStateSource? displayStateSource;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<Exception>? errorHandler;
    private readonly Channel<byte> policyChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private LightingPowerPolicy currentPolicy = LightingPowerPolicy.Disabled;
    private int connected;
    private int displayOnState = -1;
    private int lastSentIdleState = -1;
    private int lastSentDisplayState = -1;

    public ServiceLightingIdlePolicyLoop(
        IServiceClient serviceClient,
        IUserIdleTimeSource idleTimeSource,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null,
        IUserDisplayStateSource? displayStateSource = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.idleTimeSource = idleTimeSource
            ?? throw new ArgumentNullException(nameof(idleTimeSource));
        this.statusWatcher = statusWatcher ?? serviceClient;
        this.displayStateSource = displayStateSource;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task watch = WatchStatusAsync(linked.Token);
        Task poll = PollAsync(linked.Token);
        Task display = displayStateSource is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
            : WatchDisplayStateAsync(linked.Token);
        Task completed = await Task.WhenAny(watch, poll, display).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watch, poll, display).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }

        if (completed.IsFaulted)
        {
            await completed.ConfigureAwait(false);
        }
    }

    private async Task WatchStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (update)
            {
                case ServiceWatchUpdate.Connected connectedUpdate:
                    LightingPowerPolicy nextPolicy =
                        connectedUpdate.Status.Profiles.LightingPowerPolicyFor(
                            connectedUpdate.Status.ActivePowerSource);
                    LightingPowerPolicy previousPolicy = Interlocked.Exchange(
                        ref currentPolicy,
                        nextPolicy);
                    bool newlyConnected = Interlocked.Exchange(ref connected, 1) == 0;
                    if (newlyConnected)
                    {
                        Interlocked.Exchange(ref lastSentIdleState, -1);
                        Interlocked.Exchange(ref lastSentDisplayState, -1);
                    }
                    if (newlyConnected || previousPolicy != nextPolicy)
                    {
                        SignalPolicyChanged();
                    }
                    break;
                case ServiceWatchUpdate.Reconnecting:
                    Volatile.Write(ref connected, 0);
                    Volatile.Write(ref currentPolicy, LightingPowerPolicy.Disabled);
                    Interlocked.Exchange(ref lastSentIdleState, -1);
                    Interlocked.Exchange(ref lastSentDisplayState, -1);
                    SignalPolicyChanged();
                    break;
            }
        }
    }

    private async Task WatchDisplayStateAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await foreach (bool displayOn in displayStateSource!
                    .WatchDisplayOnAsync(cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    int encoded = displayOn ? 1 : 0;
                    if (Interlocked.Exchange(ref displayOnState, encoded) != encoded)
                    {
                        SignalPolicyChanged();
                    }
                }

                throw new InvalidOperationException(
                    "The Windows session display-state watcher stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
                await Task.Delay(UnavailableIdleRetryInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Volatile.Read(ref connected) == 0)
            {
                _ = await policyChanges.Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // This iteration is already reading the latest volatile policy.
            // Consume the notification that woke it so the same notification
            // cannot immediately cancel the newly calculated sleep interval.
            while (policyChanges.Reader.TryRead(out _))
            {
            }

            try
            {
                LightingPowerPolicy policy = Volatile.Read(ref currentPolicy);
                TimeSpan? idle = policy.IdleTimeoutMinutes.HasValue
                    ? idleTimeSource.GetIdleTime()
                    : null;
                bool shouldSuppress = policy.IdleTimeoutMinutes is int minutes
                    && idle is { } elapsed
                    && elapsed >= TimeSpan.FromMinutes(minutes);
                int requestedState = shouldSuppress ? 1 : 0;
                bool requestFailed = false;
                if (Volatile.Read(ref lastSentIdleState) != requestedState)
                {
                    ControlOperationResult result = await serviceClient
                        .SetLightingSuppressionAsync(
                            LightingSuppressionReason.UserIdle,
                            shouldSuppress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Status is
                        ControlOperationStatus.Applied or ControlOperationStatus.Stored)
                    {
                        Interlocked.Exchange(ref lastSentIdleState, requestedState);
                    }
                    else
                    {
                        requestFailed = true;
                        ReportFailure(new InvalidOperationException(result.Message));
                    }
                }

                int displayOn = Volatile.Read(ref displayOnState);
                if (displayOn >= 0)
                {
                    bool shouldSuppressForDisplay = displayOn == 0;
                    int requestedDisplayState = shouldSuppressForDisplay ? 1 : 0;
                    if (Volatile.Read(ref lastSentDisplayState) != requestedDisplayState)
                    {
                        ControlOperationResult result = await serviceClient
                            .SetLightingSuppressionAsync(
                                LightingSuppressionReason.DisplayOff,
                                shouldSuppressForDisplay,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Status is
                            ControlOperationStatus.Applied or ControlOperationStatus.Stored)
                        {
                            Interlocked.Exchange(
                                ref lastSentDisplayState,
                                requestedDisplayState);
                        }
                        else
                        {
                            requestFailed = true;
                            ReportFailure(new InvalidOperationException(result.Message));
                        }
                    }
                }

                TimeSpan? nextEvaluation = requestFailed
                    ? UnavailableIdleRetryInterval
                    : NextEvaluationDelay(policy, idle, shouldSuppress);
                if (nextEvaluation is null)
                {
                    _ = await policyChanges.Reader.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await WaitForDelayOrPolicyChangeAsync(
                        nextEvaluation.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
                await WaitForDelayOrPolicyChangeAsync(
                    UnavailableIdleRetryInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan? NextEvaluationDelay(
        LightingPowerPolicy policy,
        TimeSpan? idle,
        bool suppressed)
    {
        if (policy.IdleTimeoutMinutes is not int minutes)
        {
            return null;
        }
        if (idle is null)
        {
            return UnavailableIdleRetryInterval;
        }
        if (suppressed)
        {
            // Once the keyboard is off, keep the one-second activity response
            // so the first user input restores it promptly.
            return PollInterval;
        }

        TimeSpan remaining = TimeSpan.FromMinutes(minutes) - idle.Value;
        return remaining <= PollInterval
            ? PollInterval
            : remaining;
    }

    private async Task WaitForDelayOrPolicyChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task intervalTask = delay(interval, waitCancellation.Token);
        Task<bool> policyTask = policyChanges.Reader
            .WaitToReadAsync(waitCancellation.Token)
            .AsTask();
        Task completed = await Task.WhenAny(intervalTask, policyTask).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(completed, policyTask)
                && await policyTask.ConfigureAwait(false))
            {
                while (policyChanges.Reader.TryRead(out _))
                {
                }
            }
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(intervalTask, policyTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SignalPolicyChanged() => policyChanges.Writer.TryWrite(0);

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }
}

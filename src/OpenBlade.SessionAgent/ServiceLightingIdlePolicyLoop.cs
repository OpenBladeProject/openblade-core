using System.ComponentModel;
using System.Runtime.InteropServices;
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
/// Evaluates the captured Synapse idle-lighting rule in the signed-in user
/// session, where GetLastInputInfo reflects the actual interactive desktop.
/// Only a typed suppression reason crosses the service boundary.
/// </summary>
public sealed class ServiceLightingIdlePolicyLoop
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IUserIdleTimeSource idleTimeSource;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<Exception>? errorHandler;
    private LightingPowerPolicy currentPolicy = LightingPowerPolicy.Disabled;
    private int connected;
    private int lastSentState = -1;

    public ServiceLightingIdlePolicyLoop(
        IServiceClient serviceClient,
        IUserIdleTimeSource idleTimeSource,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.idleTimeSource = idleTimeSource
            ?? throw new ArgumentNullException(nameof(idleTimeSource));
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task watch = WatchStatusAsync(linked.Token);
        Task poll = PollAsync(linked.Token);
        Task completed = await Task.WhenAny(watch, poll).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watch, poll).ConfigureAwait(false);
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
        await foreach (ServiceWatchUpdate update in serviceClient
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (update)
            {
                case ServiceWatchUpdate.Connected connectedUpdate:
                    Volatile.Write(
                        ref currentPolicy,
                        connectedUpdate.Status.Profiles.EffectiveLightingPowerPolicy);
                    if (Interlocked.Exchange(ref connected, 1) == 0)
                    {
                        Interlocked.Exchange(ref lastSentState, -1);
                    }
                    break;
                case ServiceWatchUpdate.Reconnecting:
                    Volatile.Write(ref connected, 0);
                    Volatile.Write(ref currentPolicy, LightingPowerPolicy.Disabled);
                    Interlocked.Exchange(ref lastSentState, -1);
                    break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await delay(PollInterval, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref connected) == 0)
            {
                continue;
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
                if (Volatile.Read(ref lastSentState) == requestedState)
                {
                    continue;
                }

                ControlOperationResult result = await serviceClient
                    .SetLightingSuppressionAsync(
                        LightingSuppressionReason.UserIdle,
                        shouldSuppress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status is
                    ControlOperationStatus.Applied or ControlOperationStatus.Stored)
                {
                    Interlocked.Exchange(ref lastSentState, requestedState);
                }
                else
                {
                    ReportFailure(new InvalidOperationException(result.Message));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

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

using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IReactiveKeyboardSource : IDisposable;

public interface IReactiveKeyboardSourceFactory
{
    IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish);

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut) =>
        Create(publish);
}

/// <summary>
/// Owns the one session-local Raw Input registration used by Reactive lighting
/// and the captured performance shortcuts. The SessionAgent remains running
/// when the visible tray exits, so neither behavior depends on the UI process.
/// </summary>
public sealed class ServiceReactiveLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IReactiveKeyboardSourceFactory sourceFactory;
    private readonly ReactiveKeyPulsePump pulsePump;
    private readonly PerformanceShortcutTriggerPump shortcutPump;
    private readonly Action<Exception>? errorHandler;
    private IReactiveKeyboardSource? source;
    private int reactiveActive;
    private int disposed;

    public ServiceReactiveLightingLoop(
        IServiceClient serviceClient,
        IReactiveKeyboardSourceFactory sourceFactory,
        Action<Exception>? errorHandler = null)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        this.errorHandler = errorHandler;
        pulsePump = new ReactiveKeyPulsePump(SendPulseAsync, errorHandler: ReportFailure);
        shortcutPump = new PerformanceShortcutTriggerPump(
            SendPerformanceShortcutAsync,
            errorHandler: ReportFailure);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await foreach (ServiceWatchUpdate update in serviceClient
                        .WatchStatusAsync(cancellationToken)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        switch (update)
                        {
                            case ServiceWatchUpdate.Connected connected:
                                SetInputState(
                                    connected: true,
                                    reactive: connected.Status.LightingRendererOwnership ==
                                        LightingRendererOwnershipState.Reactive);
                                break;
                            case ServiceWatchUpdate.Reconnecting:
                                SetInputState(connected: false, reactive: false);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SetInputState(connected: false, reactive: false);
                    ReportFailure(exception);
                    await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            SetInputState(connected: false, reactive: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        SetInputState(connected: false, reactive: false);
        await pulsePump.DisposeAsync().ConfigureAwait(false);
        await shortcutPump.DisposeAsync().ConfigureAwait(false);
    }

    private void SetInputState(bool connected, bool reactive)
    {
        Interlocked.Exchange(ref reactiveActive, reactive ? 1 : 0);
        if (!reactive)
        {
            pulsePump.ClearPending();
        }

        if (!connected || Volatile.Read(ref disposed) != 0)
        {
            IReactiveKeyboardSource? previous = Interlocked.Exchange(ref source, null);
            previous?.Dispose();
            pulsePump.ClearPending();
            shortcutPump.ClearPending();
            return;
        }

        if (Volatile.Read(ref source) is not null)
        {
            return;
        }

        try
        {
            IReactiveKeyboardSource created = sourceFactory.Create(
                pulse =>
                {
                    if (Volatile.Read(ref reactiveActive) != 0
                        && !pulsePump.TryEnqueue(pulse))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Reactive keyboard input could not keep up and skipped a key press."));
                    }
                },
                shortcut =>
                {
                    if (!shortcutPump.TryEnqueue(shortcut))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Performance shortcut input could not keep up and skipped a button press."));
                    }
                });
            if (Volatile.Read(ref disposed) != 0
                || Interlocked.CompareExchange(ref source, created, null) is not null)
            {
                created.Dispose();
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task SendPulseAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .PulseReactiveKeyAsync(pulse, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != ControlOperationStatus.Applied)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private async Task SendPerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .CyclePerformanceShortcutAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == ControlOperationStatus.Failed)
        {
            throw new InvalidOperationException(result.Message);
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

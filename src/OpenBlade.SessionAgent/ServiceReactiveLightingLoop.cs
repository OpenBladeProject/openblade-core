using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IReactiveKeyboardSource : IDisposable;

public interface IReactiveKeyboardSourceFactory
{
    IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish);
}

/// <summary>
/// Owns session-local key observation for Reactive lighting. The SessionAgent
/// remains running when the visible tray exits, so profile automation does not
/// lose Reactive key pulses with the UI process.
/// </summary>
public sealed class ServiceReactiveLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IReactiveKeyboardSourceFactory sourceFactory;
    private readonly ReactiveKeyPulsePump pulsePump;
    private readonly Action<Exception>? errorHandler;
    private IReactiveKeyboardSource? source;
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
                                SetReactiveActive(
                                    connected.Status.LightingRendererOwnership ==
                                    LightingRendererOwnershipState.Reactive);
                                break;
                            case ServiceWatchUpdate.Reconnecting:
                                SetReactiveActive(false);
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
                    SetReactiveActive(false);
                    ReportFailure(exception);
                    await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            SetReactiveActive(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        SetReactiveActive(false);
        await pulsePump.DisposeAsync().ConfigureAwait(false);
    }

    private void SetReactiveActive(bool active)
    {
        if (!active || Volatile.Read(ref disposed) != 0)
        {
            IReactiveKeyboardSource? previous = Interlocked.Exchange(ref source, null);
            previous?.Dispose();
            pulsePump.ClearPending();
            return;
        }

        if (Volatile.Read(ref source) is not null)
        {
            return;
        }

        try
        {
            IReactiveKeyboardSource created = sourceFactory.Create(pulse =>
            {
                if (!pulsePump.TryEnqueue(pulse))
                {
                    ReportFailure(new InvalidOperationException(
                        "Reactive keyboard input could not keep up and skipped a key press."));
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

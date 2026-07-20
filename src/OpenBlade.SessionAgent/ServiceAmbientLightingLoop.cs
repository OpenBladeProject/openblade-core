using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Samples the interactive desktop only while Ambient Awareness owns the
/// keyboard renderer. Only a keyboard-sized RGB frame crosses the service IPC.
/// </summary>
public sealed class ServiceAmbientLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IAmbientScreenSamplerFactory samplerFactory;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object stateSync = new();
    private CancellationTokenSource? rendererCancellation;
    private Task? rendererTask;
    private AmbientAwarenessRegion? activeRegion;
    private int disposed;

    public ServiceAmbientLightingLoop(
        IServiceClient serviceClient,
        IAmbientScreenSamplerFactory samplerFactory,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.samplerFactory = samplerFactory
            ?? throw new ArgumentNullException(nameof(samplerFactory));
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
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
                        AmbientAwarenessRegion? region = update switch
                        {
                            ServiceWatchUpdate.Connected connected
                                when connected.Status.LightingRendererOwnership ==
                                    LightingRendererOwnershipState.AmbientAwareness
                                && connected.Status.DesiredState.Lighting.Value.Parameters is
                                    AmbientAwarenessLightingParameters parameters =>
                                parameters.Region,
                            _ => null,
                        };
                        await SetRegionAsync(region, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await SetRegionAsync(null, CancellationToken.None).ConfigureAwait(false);
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await SetRegionAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await SetRegionAsync(null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SetRegionAsync(
        AmbientAwarenessRegion? region,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation;
        Task? previousTask;
        lock (stateSync)
        {
            if (activeRegion == region
                && (region is null || rendererTask is { IsCompleted: false }))
            {
                return;
            }

            previousCancellation = rendererCancellation;
            previousTask = rendererTask;
            rendererCancellation = null;
            rendererTask = null;
            activeRegion = null;
        }

        previousCancellation?.Cancel();
        if (previousTask is not null)
        {
            try
            {
                await previousTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        previousCancellation?.Dispose();

        if (region is null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task task = RenderAsync(region.Value, linked.Token);
        lock (stateSync)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                rendererCancellation = linked;
                rendererTask = task;
                activeRegion = region;
                return;
            }
        }

        linked.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        linked.Dispose();
    }

    private async Task RenderAsync(
        AmbientAwarenessRegion region,
        CancellationToken cancellationToken)
    {
        using IAmbientScreenSampler sampler = samplerFactory.Create();
        RgbColor[]? previous = null;
        DateTimeOffset lastSent = DateTimeOffset.MinValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AmbientLightingFrame frame = sampler.Capture(region);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool changed = previous is null || !frame.Slots.SequenceEqual(previous);
                if (changed || now - lastSent >= KeepaliveInterval)
                {
                    ControlOperationResult result = await serviceClient
                        .PushAmbientLightingFrameAsync(frame, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Status != ControlOperationStatus.Applied)
                    {
                        throw new InvalidOperationException(result.Message);
                    }

                    previous = frame.Slots.ToArray();
                    lastSent = now;
                }

                await delay(FrameInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
                await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
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

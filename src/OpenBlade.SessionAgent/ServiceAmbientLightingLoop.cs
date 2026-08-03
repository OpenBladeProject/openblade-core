using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Samples the interactive desktop only while Ambient Awareness owns the
/// keyboard renderer. Only a keyboard-sized RGB frame crosses the service IPC.
/// </summary>
public sealed class ServiceAmbientLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IAmbientScreenSamplerFactory samplerFactory;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim rendererStateGate = new(1, 1);
    private CancellationTokenSource? rendererCancellation;
    private Task? rendererTask;
    private AmbientAwarenessRegion? activeRegion;
    private int disposed;

    public ServiceAmbientLightingLoop(
        IServiceClient serviceClient,
        IAmbientScreenSamplerFactory samplerFactory,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null,
        TimeProvider? timeProvider = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.samplerFactory = samplerFactory
            ?? throw new ArgumentNullException(nameof(samplerFactory));
        this.statusWatcher = statusWatcher ?? serviceClient;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
                    await foreach (ServiceWatchUpdate update in statusWatcher
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
        await rendererStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeRegion == region
                && (region is null || rendererTask is { IsCompleted: false }))
            {
                return;
            }

            CancellationTokenSource? previousCancellation = rendererCancellation;
            Task? previousTask = rendererTask;
            rendererCancellation = null;
            rendererTask = null;
            activeRegion = null;
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
            rendererCancellation = linked;
            rendererTask = RenderAsync(region.Value, linked.Token);
            activeRegion = region;
        }
        finally
        {
            rendererStateGate.Release();
        }
    }

    private async Task RenderAsync(
        AmbientAwarenessRegion region,
        CancellationToken cancellationToken)
    {
        IAmbientScreenSampler? sampler = null;
        var cadence = new SessionLightingFrameCadence(timeProvider);
        // Keep sampler ownership parallel with ServiceAudioMeterLightingLoop:
        // native sampler failures recreate resources, while service failures retain them.
        try
        {
            while (true)
            {
                AmbientLightingFrame frame;
                try
                {
                    sampler ??= samplerFactory.Create();
                    frame = sampler.Capture(region);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ResetSampler(ref sampler);
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    SessionLightingFrameDecision decision = cadence.Evaluate(frame);
                    if (decision.ShouldSend)
                    {
                        ControlOperationResult result = await serviceClient
                            .PushAmbientLightingFrameAsync(frame, cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Status != ControlOperationStatus.Applied)
                        {
                            throw new InvalidOperationException(result.Message);
                        }

                        cadence.RecordSent(frame);
                    }

                    await delay(decision.NextInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
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
        finally
        {
            ResetSampler(ref sampler);
        }
    }

    private void ResetSampler(ref IAmbientScreenSampler? sampler)
    {
        IAmbientScreenSampler? previous = sampler;
        sampler = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Dispose();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
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

internal readonly record struct SessionLightingFrameDecision(
    bool ShouldSend,
    TimeSpan NextInterval);

internal sealed class SessionLightingFrameCadence(TimeProvider timeProvider)
{
    // Match the matrix renderer's proven 30 FPS cadence while the sampled
    // colors are changing. After roughly half a second without a change,
    // reduce captures to 5 FPS; the next changed sample immediately restores
    // the active cadence.
    internal const int StaticFrameThreshold = 15;
    internal static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ActiveInterval = TimeSpan.FromTicks(
        TimeSpan.TicksPerSecond / 30);
    internal static readonly TimeSpan StaticInterval = TimeSpan.FromMilliseconds(200);

    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private IReadOnlyList<RgbColor>? lastSentFrame;
    private DateTimeOffset? lastSentAt;
    private int unchangedFrames;

    internal SessionLightingFrameDecision Evaluate(AmbientLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool changed = lastSentFrame is null
            || !FramesEqual(frame.Slots, lastSentFrame);
        TimeSpan nextInterval;
        if (changed)
        {
            unchangedFrames = 0;
            nextInterval = ActiveInterval;
        }
        else
        {
            unchangedFrames = Math.Min(StaticFrameThreshold, unchangedFrames + 1);
            nextInterval = unchangedFrames >= StaticFrameThreshold
                ? StaticInterval
                : ActiveInterval;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        bool shouldSend = changed
            || lastSentAt is null
            || now - lastSentAt >= KeepaliveInterval;
        return new(shouldSend, nextInterval);
    }

    internal void RecordSent(AmbientLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lastSentFrame = frame.Slots;
        lastSentAt = timeProvider.GetUtcNow();
    }

    private static bool FramesEqual(
        IReadOnlyList<RgbColor> left,
        IReadOnlyList<RgbColor> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}

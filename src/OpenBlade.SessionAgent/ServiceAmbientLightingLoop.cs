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
    private AmbientRenderingConfiguration? activeConfiguration;
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
                        AmbientRenderingConfiguration? configuration = update switch
                        {
                            ServiceWatchUpdate.Connected connected
                                when connected.Status.LightingRendererOwnership ==
                                    LightingRendererOwnershipState.AmbientAwareness
                                && connected.Status.DesiredState.Lighting.Value.Parameters is
                                    AmbientAwarenessLightingParameters parameters =>
                                new AmbientRenderingConfiguration(
                                    parameters.Region,
                                    connected.Status.LightingRefreshRate),
                            _ => null,
                        };
                        await SetConfigurationAsync(configuration, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await SetConfigurationAsync(null, CancellationToken.None)
                        .ConfigureAwait(false);
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await SetConfigurationAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await SetConfigurationAsync(null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SetConfigurationAsync(
        AmbientRenderingConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        await rendererStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeConfiguration == configuration
                && (configuration is null || rendererTask is { IsCompleted: false }))
            {
                return;
            }

            CancellationTokenSource? previousCancellation = rendererCancellation;
            Task? previousTask = rendererTask;
            rendererCancellation = null;
            rendererTask = null;
            activeConfiguration = null;
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
            if (configuration is null || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rendererCancellation = linked;
            rendererTask = RenderAsync(configuration.Value, linked.Token);
            activeConfiguration = configuration;
        }
        finally
        {
            rendererStateGate.Release();
        }
    }

    private async Task RenderAsync(
        AmbientRenderingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IAmbientScreenSampler? sampler = null;
        var cadence = new SessionLightingFrameCadence(
            timeProvider,
            configuration.RefreshRate);
        var pacer = new SessionLightingFramePacer(
            timeProvider,
            delay,
            configuration.RefreshRate);
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
                    frame = sampler.Capture(configuration.Region);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ResetSampler(ref sampler);
                    pacer.Reset();
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

                    await pacer.WaitAsync(decision.NextInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    pacer.Reset();
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

internal readonly record struct AmbientRenderingConfiguration(
    AmbientAwarenessRegion Region,
    LightingRefreshRate RefreshRate);

internal readonly record struct SessionLightingFrameDecision(
    bool ShouldSend,
    TimeSpan NextInterval);

internal sealed class SessionLightingFramePacer(
    TimeProvider timeProvider,
    Func<TimeSpan, CancellationToken, Task> delay,
    LightingRefreshRate refreshRate)
{
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Func<TimeSpan, CancellationToken, Task> delay =
        delay ?? throw new ArgumentNullException(nameof(delay));
    private readonly LightingRefreshRate refreshRate = Enum.IsDefined(refreshRate)
        ? refreshRate
        : throw new ArgumentOutOfRangeException(nameof(refreshRate));
    private long originTimestamp = timeProvider.GetTimestamp();
    private TimeSpan? nextDeadline;
    private TimeSpan? lastInterval;

    internal async Task WaitAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (refreshRate == LightingRefreshRate.ThirtyHz)
        {
            await delay(interval, cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan elapsed = timeProvider.GetElapsedTime(originTimestamp);
        if (nextDeadline is null || lastInterval != interval)
        {
            nextDeadline = elapsed + interval;
        }
        else
        {
            nextDeadline += interval;
            while (nextDeadline <= elapsed)
            {
                nextDeadline += interval;
            }
        }

        lastInterval = interval;
        TimeSpan remaining = nextDeadline.Value - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Reset()
    {
        originTimestamp = timeProvider.GetTimestamp();
        nextDeadline = null;
        lastInterval = null;
    }
}

internal sealed class SessionLightingFrameCadence
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

    private readonly TimeProvider timeProvider;
    private readonly TimeSpan activeInterval;
    private readonly int staticFrameThreshold;
    private IReadOnlyList<RgbColor>? lastSentFrame;
    private DateTimeOffset? lastSentAt;
    private int unchangedFrames;

    internal SessionLightingFrameCadence(
        TimeProvider timeProvider,
        LightingRefreshRate refreshRate = LightingRefreshRate.ThirtyHz)
    {
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        activeInterval = refreshRate switch
        {
            LightingRefreshRate.ThirtyHz => ActiveInterval,
            LightingRefreshRate.SixtyHz => TimeSpan.FromTicks(
                TimeSpan.TicksPerSecond / 60),
            _ => throw new ArgumentOutOfRangeException(nameof(refreshRate)),
        };
        staticFrameThreshold = refreshRate == LightingRefreshRate.SixtyHz
            ? StaticFrameThreshold * 2
            : StaticFrameThreshold;
    }

    internal SessionLightingFrameDecision Evaluate(AmbientLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool changed = lastSentFrame is null
            || !FramesEqual(frame.Slots, lastSentFrame);
        TimeSpan nextInterval;
        if (changed)
        {
            unchangedFrames = 0;
            nextInterval = activeInterval;
        }
        else
        {
            unchangedFrames = Math.Min(staticFrameThreshold, unchangedFrames + 1);
            nextInterval = unchangedFrames >= staticFrameThreshold
                ? StaticInterval
                : activeInterval;
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

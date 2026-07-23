namespace OpenBlade.Service;

public sealed class ServicePeriodicObservationLoop
{
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceControlRuntime runtime;
    private readonly ILogger logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        ILogger logger)
        : this(runtime, logger, static (interval, token) => Task.Delay(interval, token))
    {
    }

    internal ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await delay(ObservationInterval, stoppingToken);
            if (!await RunStepAsync(
                runtime.RefreshPowerSourceClassificationAsync,
                "power-source classification",
                stoppingToken))
            {
                return;
            }
            if (!await RunStepAsync(
                runtime.MaintainDriverModeAsync,
                "keyboard Driver mode",
                stoppingToken))
            {
                return;
            }
            if (!await RunStepAsync(
                runtime.RestoreLightingRendererAsync,
                "lighting renderer",
                stoppingToken))
            {
                return;
            }
        }
    }

    private async Task<bool> RunStepAsync(
        Func<CancellationToken, Task> step,
        string name,
        CancellationToken stoppingToken)
    {
        try
        {
            await step(stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Periodic {ObservationStep} maintenance failed.",
                name);
            return true;
        }
    }
}

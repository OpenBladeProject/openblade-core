namespace OpenBlade.Service;

public sealed class ServicePeriodicObservationLoop
{
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceControlRuntime runtime;
    private readonly LowPowerGate lowPowerGate;
    private readonly ILogger logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        LowPowerGate lowPowerGate,
        ILogger logger)
        : this(
            runtime,
            lowPowerGate,
            logger,
            static (interval, token) => Task.Delay(interval, token))
    {
    }

    public ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        ILogger logger)
        : this(runtime, new LowPowerGate(), logger)
    {
    }

    internal ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        LowPowerGate lowPowerGate,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.lowPowerGate = lowPowerGate
            ?? throw new ArgumentNullException(nameof(lowPowerGate));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    internal ServicePeriodicObservationLoop(
        IServiceControlRuntime runtime,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task> delay)
        : this(runtime, new LowPowerGate(), logger, delay)
    {
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
            if (lowPowerGate.IsActive)
            {
                logger.LogInformation(
                    "Skipping periodic keyboard Driver-mode maintenance while the low-power gate is active.");
                logger.LogInformation(
                    "Skipping periodic lighting-renderer restoration while the low-power gate is active.");
            }
            else
            {
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

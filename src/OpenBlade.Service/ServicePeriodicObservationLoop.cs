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

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        bool lowPowerSkipLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            await delay(ObservationInterval, stoppingToken);
            if (lowPowerGate.IsActive)
            {
                if (!lowPowerSkipLogged)
                {
                    logger.LogInformation(
                        "Skipping periodic power classification, keyboard Driver-mode maintenance, and lighting-renderer restoration while the low-power gate is active.");
                    lowPowerSkipLogged = true;
                }

                continue;
            }

            lowPowerSkipLogged = false;
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
            if (runtime.ShouldRunThermalConvergenceObservation()
                && !await RunStepAsync(
                    token => runtime.ObserveAsync(
                        includeLiveReadings: false,
                        token),
                    "thermal desired-state observation",
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

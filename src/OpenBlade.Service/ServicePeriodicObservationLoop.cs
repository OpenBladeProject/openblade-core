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
            try
            {
                await runtime.RefreshPowerSourceClassificationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic service state observation failed.");
            }
        }
    }
}

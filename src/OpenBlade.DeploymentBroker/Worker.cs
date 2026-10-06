using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public sealed class Worker(
    BrokerEnrollmentStore enrollmentStore,
    IBrokerClientContextResolver clientContextResolver,
    IActiveConsoleSessionSource activeConsole,
    IBrokerRequestHandler requestHandler,
    BrokerDurableRecordStore durableRecords,
    IBrokerStartupReconciler startupReconciler,
    BrokerPaths paths,
    ILogger<Worker> logger) : BackgroundService
{
    public const string PipeName = "OpenBlade.DeploymentBroker.v1";
    internal static readonly TimeSpan PipeFailureRetryDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BrokerEnrollment enrollment;
        try
        {
            enrollment = await enrollmentStore.LoadAsync(stoppingToken).ConfigureAwait(false);
            BrokerTrustPolicyValidator.Validate(new BrokerTrustConfiguration(
                "openblade-local-dev-v1",
                new OpenBladeReleaseTrustPolicy(
                    true,
                    [
                        new OpenBladeReleaseTrustKey(
                            enrollment.DevelopmentSigningKeyId,
                            enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64),
                    ])));

            await durableRecords.ValidateExistingJournalsAsync(stoppingToken)
                .ConfigureAwait(false);
            if (!await startupReconciler.TryReconcileAsync(enrollment, stoppingToken)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "An unresolved deployment blocks unattended broker startup.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or OpenBladeReleaseVerificationException)
        {
            logger.LogCritical(
                exception,
                "The deployment broker refused startup because enrollment or trust is invalid.");
            throw new InvalidOperationException(
                "The deployment broker is not safely enrolled and enabled.",
                exception);
        }

        BrokerPipeServer server = new(
            PipeName,
            enrollment,
            clientContextResolver,
            activeConsole,
            new BrokerReplayStore(paths),
            new BrokerSingleFlightGate(),
            requestHandler);
        await RunPipeLoopAsync(
            server.ServeOnceAsync,
            Task.Delay,
            exception => logger.LogWarning(
                exception,
                "A deployment broker pipe request failed closed."),
            stoppingToken).ConfigureAwait(false);
    }

    internal static async Task RunPipeLoopAsync(
        Func<CancellationToken, Task> serveOnceAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<Exception> logPipeFailure,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await serveOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is
                IOException or TimeoutException or UnauthorizedAccessException)
            {
                logPipeFailure(exception);
                try
                {
                    await delayAsync(PipeFailureRetryDelay, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}

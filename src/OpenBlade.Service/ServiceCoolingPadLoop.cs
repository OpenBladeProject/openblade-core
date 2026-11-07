using Microsoft.Extensions.Logging;

namespace OpenBlade.Service;

/// <summary>One bounded latest-frame pump; curve temperature work remains limited to one tick per second.</summary>
internal sealed class ServiceCoolingPadLoop(IServiceControlRuntime runtime, ILogger logger)
{
    internal async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await runtime.MaintainCoolingPadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning(exception, "Cooling pad maintenance failed."); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

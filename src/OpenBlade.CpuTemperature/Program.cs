using OpenBlade.Platform;

namespace OpenBlade.CpuTemperature;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args is not ["--worker"]) { return 2; }
        return await CpuTemperatureWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
            () => new LhmCpuTemperatureSession(), () => PawnIoRuntimeAdmission.VerifyAvailable());
    }
}

internal interface ICpuTemperatureSession : IDisposable
{
    int? ReadMilliCelsius();
}

internal static class CpuTemperatureWorker
{
    // A helper is demand driven and leaves no sensor session running after its
    // caller stops requesting samples. Its parent owns the kill-on-close job.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync(Stream input, Stream output,
        Func<ICpuTemperatureSession> createSession, Action verifyDriver,
        CancellationToken cancellationToken = default)
    {
        ICpuTemperatureSession? session = null;
        long previousSequence = 0;
        byte[] request = new byte[CpuTemperatureProtocol.RequestLength];
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                // Redirected Windows console streams can perform synchronous reads
                // that ignore cancellation. Bound our wait independently, and exit
                // after timeout rather than starting another overlapping reader.
                Task read = Task.Run(async () =>
                    await input.ReadExactlyAsync(request, idle.Token).ConfigureAwait(false));
                _ = read.ContinueWith(completed => _ = completed.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try
                {
                    await read.WaitAsync(idle.Token).ConfigureAwait(false);
                    idle.Token.ThrowIfCancellationRequested();
                }
                catch (EndOfStreamException) { return 0; }
                catch (OperationCanceledException) { return 0; }
                if (!CpuTemperatureProtocol.TryDecodeRequest(request, out long sequence)
                    || sequence <= previousSequence) { return 3; }
                previousSequence = sequence;

                int? value;
                try
                {
                    // No driver/library operation occurs before a request. The
                    // parent sends that request only after assigning our job.
                    if (session is null)
                    {
                        verifyDriver();
                        session = createSession();
                        // Readiness never carries a temperature. The parent must issue
                        // a new request after cold initialization to obtain a fresh sample.
                        await output.WriteAsync(CpuTemperatureProtocol.EncodeReadyReply(sequence),
                            cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    value = session.ReadMilliCelsius();
                }
                catch (Exception)
                {
                    // No partial or prior sensor value can survive a failed read.
                    await output.WriteAsync(CpuTemperatureProtocol.EncodeTerminalReply(sequence),
                        cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return 4;
                }
                await output.WriteAsync(CpuTemperatureProtocol.EncodeReply(sequence, value),
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) { return 4; }
        finally { session?.Dispose(); }
    }
}

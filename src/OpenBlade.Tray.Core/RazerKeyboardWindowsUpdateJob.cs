using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Bounded asynchronous WUA job lifetime; injectable delegates keep timeout/abort tests native-free.</summary>
internal static class RazerKeyboardWindowsUpdateJob
{
    internal static async Task<object> RunAsync(Func<object> begin, Func<object, object> end,
        Action<object> abort, Action<object> cleanup, Task completion, TimeSpan timeout,
        CancellationToken cancellationToken, Action finished, Action<object>? lateResult = null)
    {
        object job;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            job = begin();
        }
        catch { finished(); throw; }
        try
        {
            await completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            int abortError = 0;
            try { abort(job); }
            catch (Exception abortFailure) { abortError = abortFailure.HResult; }
            // CleanUp may block until completion. Native references remain owned until FinishAbortedAsync ends.
            _ = FinishAbortedAsync();
            throw new RazerKeyboardWindowsUpdateException($"{ex.GetType().Name}; native abort requested (HRESULT 0x{abortError:X8}). Completion and installed state remain unresolved.",
                ex is TimeoutException ? unchecked((int)0x800705B4) : unchecked((int)0x800704C7));
        }
        try { return end(job); }
        finally
        {
            try { cleanup(job); }
            finally { finished(); }
        }

        async Task FinishAbortedAsync()
        {
            try
            {
                await completion.ConfigureAwait(false);
                object result = end(job);
                lateResult?.Invoke(result);
            }
            catch (Exception ex)
            {
                DiagnosticTrace.TraceException("SnapTapWindowsUpdate", "NativeCompletionAfterAbort", ex);
            }
            finally
            {
                try { cleanup(job); }
                catch (Exception) { /* Cleanup cannot change the already recorded uncertain outcome. */ }
                finally { finished(); }
            }
        }
    }
}

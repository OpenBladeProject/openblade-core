#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Text;

namespace OpenBlade.DeploymentBroker;

// One continuously running reader; retain only the tail, never a queue of lines.
internal sealed class ScratchDiagnosticDrain
{
    internal const int Capacity = 8192;
    private readonly StringBuilder tail = new(Capacity);
    private readonly CancellationTokenSource stop = new();
    private readonly Task draining;
    private bool truncated;

    internal ScratchDiagnosticDrain(TextReader reader) => draining = DrainAsync(reader);

    private async Task DrainAsync(TextReader reader)
    {
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) != 0)
        {
            lock (tail)
            {
                int excess = tail.Length + count - Capacity;
                if (excess > 0) { tail.Remove(0, excess); truncated = true; }
                tail.Append(buffer, 0, count);
            }
        }
    }

    internal string Snapshot()
    {
        lock (tail) { return (truncated ? "[earlier stderr truncated]\n" : "") + tail; }
    }

    // Called after the child stop attempt and before disposing its pipe reader.
    // A broken or non-EOF stream is still a refusal, even if termination succeeded.
    internal void Finish(TextWriter destination, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        try { draining.WaitAsync(timeout ?? TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (TimeoutException error) { failure = new IOException("Scratch readiness stderr did not reach EOF.", error); }
        catch (Exception error) { failure = error; }
        ScratchFixtureFailure.Cleanup(failure, () => stop.Cancel(), () => stop.Dispose(), () => destination.Write(Snapshot()));
    }
}
#endif

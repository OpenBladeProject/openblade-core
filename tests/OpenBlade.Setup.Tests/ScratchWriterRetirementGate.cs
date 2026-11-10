namespace OpenBlade.Setup.Tests;

// Shared by both directions of the scratch handoff. The caller retains the
// outgoing process handle and independently observes SCM and recovery state.
internal static class ScratchWriterRetirementGate
{
    internal enum Backend : byte { Managed = 1, Candidate = 2 }

    internal readonly record struct Generation(Backend Backend, Guid Instance);

    internal static void RequireRetired(Generation outgoing, bool scmStopped, bool exitedCleanly,
        Func<Generation?> readReceipt, Action requireRecoveryClear)
    {
        if (!scmStopped)
        {
            throw new InvalidOperationException("Writer stop was not confirmed.");
        }
        if (!exitedCleanly)
        {
            throw new InvalidOperationException("Outgoing writer process exit was not confirmed.");
        }
        if (readReceipt() != outgoing)
        {
            throw new InvalidOperationException("Retirement receipt is missing, incomplete, or stale.");
        }
        requireRecoveryClear();
    }
}

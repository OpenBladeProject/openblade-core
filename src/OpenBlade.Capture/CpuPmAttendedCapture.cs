using System.Diagnostics;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal static class CpuPmAttendedCapture
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = Rz090528CpuPmObservationProbe.Parse(args);
        if (parsed is null) { error.WriteLine("Usage: " + Rz090528CpuPmObservationProbe.Usage); return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CpuPmLiveParentAuthority? authority = null;
        bool revokeAttempted = false;
        void RevokeOnce()
        {
            if (revokeAttempted || authority is null) { return; }
            revokeAttempted = true; authority.Dispose();
        }
        try
        {
            var bootstrap = CpuPmSupervisorBootstrap.Load(parsed.RunId, false, deadline.Token);
            var run = await WindowsCpuPmLaunchStartupWait.LoadAsync(parsed.RunId, bootstrap.Trusted, deadline.Token).ConfigureAwait(false);
            if (run is null || run.ParentIdentity != bootstrap.Record.Supervisor) { throw new IOException("Authenticated capture run unavailable."); }
            var channel = await WindowsCpuPmAuthorityChannel.ConnectChildAsync(parsed.RunId, run.ParentIdentity, deadline.Token).ConfigureAwait(false);
            authority = new CpuPmLiveParentAuthority(parsed.RunId, run.LifetimeId, run.ParentIdentity, channel, new CpuPmAuthorityClock());
            CpuPmRunOwnedDriverLifetime? Fresh()
            {
                bootstrap.AssertHeld();
                if (!authority.ReadFreshAsync(deadline.Token).GetAwaiter().GetResult()) { return null; }
                bootstrap.AssertHeld();
                return new(parsed.RunId, run.LifetimeId, true);
            }
            var lifetime = Fresh();
            if (lifetime is null) { throw new IOException("Fresh owned lifetime unavailable."); }
            var approval = CpuPmAttendedApproval.Acknowledge(true, true, lifetime);
            var sink = run.CreateWindowsEvidenceSink();
            // Synchronous engine and query mutex remain on this ONE thread for
            // the entire fixed three-sample observation. No Task.Run or retry.
            var provenance = new CpuPmCaptureProvenance(parsed.RunId, run.LifetimeId, bootstrap.Trusted.Source,
                bootstrap.Trusted.ManifestSha256, Stopwatch.Frequency);
            var stored = CaptureAndStore(() => WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(approval, Fresh, deadline.Token),
                RevokeOnce, provenance, sink, deadline.Token);
            output.WriteLine($"PmObservation={stored.Status}; Samples={stored.VerifiedSamples}; Files={stored.VerifiedFiles}");
            output.WriteLine("LivePptTarget=Unknown; IndependentRestore=Unproven");
            return stored.Status == CpuPmCaptureWriteStatus.StoredComplete ? 0 : 1;
        }
        catch (Exception)
        {
            error.WriteLine("PmObservation=Unavailable; NoRetry=True; LivePptTarget=Unknown"); return 3;
        }
        finally
        {
            // No second disposal attempt follows a failed cleanup. The parent
            // still requires actual child terminal state and listener drain;
            // this method never grants driver cleanup from a disposal result.
            if (!revokeAttempted) { try { RevokeOnce(); } catch (Exception) { } }
        }
    }
    internal static CpuPmCaptureWriteResult CaptureAndStore(Func<CpuPmObservationBatch> capture, Action revoke,
        CpuPmCaptureProvenance provenance, ICpuPmCaptureEvidenceSink sink, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CpuPmObservationBatch batch = capture();
        try { revoke(); }
        catch (Exception)
        {
            // The native observation has already returned. Retain its exact raw
            // samples, but never report completion after uncertain authority
            // cleanup. No recapture or disposal retry occurs in this path.
            batch = new(false, batch.CopySamples(), batch.CopyPciRecords(), batch.OrientationMask, batch.FuseMask,
                new(CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed));
        }
        // A returned partial/cancelled batch is persisted without new authority.
        // A never-returned native invocation has no batch to persist.
        return CpuPmReturnedBatchOutput.Write(CpuPmReturnedBatchOutput.Prepare(provenance, batch, token.IsCancellationRequested), sink);
    }
}

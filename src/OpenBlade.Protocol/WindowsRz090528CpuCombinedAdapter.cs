using System.Diagnostics;

namespace OpenBlade.Protocol;

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    // A combined CO step uses the existing exact PPT script for its read-only
    // peer sample. Both families share one adapter, actual worker and lease.
    // No service observer or second session is entered from this native scope.
    private static CpuLiveCurveNativeResult ExecuteCurveWithPowerPeer(CpuLiveCurveNativeRequest request,
        CpuLiveCurveWriteProof? proof, Func<DeviceIdentity?> readUsb, ICpuQueryWindowsPlatform platform,
        CancellationToken token)
    {
        var operation = NativeOperations.GetValue(platform, static _ => new());
        if (Interlocked.CompareExchange(ref operation.Active, 1, 0) != 0)
        { return new(CpuLiveCurveNativeDisposition.NotSubmitted, true, true, false, null, CpuLiveCurveNativeFailure.AdmissionDenied); }
        IDisposable? lease = null;
        IDisposable? readScope = null;
        WindowsRz090528CpuQueryAdapter? adapter = null;
        CpuPowerNativeWire? wire = null;
        bool cleanup = true, verified = false;
        var failure = CpuLiveCurveNativeFailure.AdmissionDenied;
        long started = Stopwatch.GetTimestamp();
        try
        {
            token.ThrowIfCancellationRequested();
            if (proof is null || !proof.Consume(request) || !ExactUsb(readUsb()) || !ExactPlatform())
            { throw new CpuPowerAdmissionException(); }
            readScope = proof.BeginRead(request);
            wire = new(request, Check, CheckPeer, BeforeSetter, BeginReadback);
            adapter = new(platform, readUsb, wire);
            lease = adapter.Acquire(TimeSpan.FromSeconds(20), token);
            if (lease is null || !ExactPlatform()) { throw new CpuPowerAdmissionException(); }
            Check();
            failure = CpuLiveCurveNativeFailure.Transport;
            wire.Run(adapter, token);
            EndRead();
            CheckFinal();
            verified = wire.Complete && CpuLiveCurveNativeRequest.Equal(wire.CurveAfter, request.After)
                && wire.After?.Pair == request.PowerPeer!.Expected;
            failure = verified ? CpuLiveCurveNativeFailure.None : CpuLiveCurveNativeFailure.ReadbackDrift;
        }
        catch (CpuPowerAdmissionException) { failure = CpuLiveCurveNativeFailure.AdmissionDenied; }
        catch (OperationCanceledException) { failure = CpuLiveCurveNativeFailure.Cancelled; }
        catch (Exception) { failure = CpuLiveCurveNativeFailure.Transport; }
        finally
        {
            wire?.Abort();
            try { readScope?.Dispose(); }
            catch { cleanup = false; }
            try { lease?.Dispose(); }
            catch { cleanup = false; }
            finally
            {
                cleanup &= adapter?.cleanupFailed != true;
                if (!cleanup) { verified = false; failure = CpuLiveCurveNativeFailure.Cleanup; }
                else { Volatile.Write(ref operation.Active, 0); }
            }
        }
        return new(wire?.Disposition ?? CpuLiveCurveNativeDisposition.NotSubmitted,
            true, cleanup, verified, wire?.CurveAfter, failure);

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(90)
                || (readScope is null ? proof?.Current(request) : proof?.ReadCurrent(request)) != true)
            { throw new CpuPowerAdmissionException(); }
        }
        void EndRead()
        {
            if (readScope is null) { return; }
            try
            {
                if (proof?.CompleteRead(request) != true) { throw new CpuPowerAdmissionException(); }
            }
            finally
            {
                try { readScope.Dispose(); }
                catch { cleanup = false; throw; }
                finally { readScope = null; }
            }
        }
        void BeforeSetter()
        {
            EndRead();
            // The read proof is gone before all original fresh setter checks.
            CheckFinal();
            if (proof?.SubmissionCurrent(request) != true) { throw new CpuPowerAdmissionException(); }
        }
        void BeginReadback() => readScope = proof!.BeginRead(request);
        bool ExactPlatform() => platform is ICpuPmWindowsPlatform pm
            && pm.ReadHost() is { Failure: null, Evidence: { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4" } }
            && pm.ReadPackage() is { MaximumExtendedLeaf: >= 0x80000001, Package: 1 };
        void CheckRange()
        {
            Check();
            if (proof!.ReadFreshRange(request)?.AllowsOperation(request) != true)
            { throw new CpuPowerAdmissionException(); }
            Check();
        }
        void CheckFinal()
        {
            if (proof?.SupportsFinalCheckpoint == true)
            {
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(90)
                    || adapter?.FreshAdmission() != true || !ExactPlatform()
                    || wire is not { Capability: uint capability, Cap: { } cap } || (capability & 4) == 0
                    || !proof.FinalCheckpointCurrent(request, capability, cap)) { throw new CpuPowerAdmissionException(); }
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(90)) { throw new CpuPowerAdmissionException(); }
                return;
            }
            Check();
            if (adapter?.FreshAdmission() != true || !ExactPlatform()) { throw new CpuPowerAdmissionException(); }
            CheckPeer();
            Check();
        }
        void CheckPeer()
        {
            CheckRange();
            if (wire is not { Capability: uint capability, Cap: { } cap }
                || (capability & 4) == 0
                || !proof!.AdmitPowerPeer(request, capability, cap)) { throw new CpuPowerAdmissionException(); }
            Check();
        }
    }
}

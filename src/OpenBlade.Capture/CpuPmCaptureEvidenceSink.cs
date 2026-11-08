using System.Security.Cryptography;

namespace OpenBlade.Capture;

// Implemented only by a future trusted launcher retaining the authenticated run
// and directory pins. No parser, command flag or driver-status proof implements it.
// Run pin validation remains available after invocation authority is revoked.
internal interface ICpuPmAuthenticatedCaptureRun
{
    Guid RunId { get; }
    void AssertPinnedRun();
    void AssertWorkerTerminal();
}

internal interface ICpuPmCaptureEvidenceFiles
{
    void BeginFreshRun(Guid runId);
    void StoreImmutable(string name, byte[] bytes, string sha256);
    void ReleaseAfterWorkerTerminal();
}

internal sealed class CpuPmCaptureEvidenceSink : ICpuPmCaptureEvidenceSink
{
    private readonly ICpuPmAuthenticatedCaptureRun? run;
    private readonly ICpuPmCaptureEvidenceFiles? files;
    private bool begun, refused, finished, released;
    private int slots, bytesReserved;
    private readonly Guid frozenRunId;
    internal const int MaximumBytes = 77804;
    internal CpuPmCaptureEvidenceSink(ICpuPmAuthenticatedCaptureRun? run, ICpuPmCaptureEvidenceFiles? files)
    { this.run = run; this.files = files; frozenRunId = run?.RunId ?? Guid.Empty; }
    internal int ReservedSlots => slots;
    internal int ReservedBytes => bytesReserved;

    public bool BeginFreshRun(Guid runId)
    {
        if (refused || begun) { refused = true; return false; }
        try
        {
            if (run is null || files is null || runId == Guid.Empty || runId != frozenRunId || run.RunId != runId) { refused = true; return false; }
            run.AssertPinnedRun();
            if (run.RunId != frozenRunId) { refused = true; return false; }
            // Mark consumed before the first create; a failed marker is permanent.
            begun = true;
            files.BeginFreshRun(runId);
            if (run.RunId != frozenRunId) { refused = true; return false; }
            return true;
        }
        catch (Exception) { refused = true; return false; }
    }

    public bool TryWriteImmutable(CpuPmEvidenceFile file)
    {
        if (refused || !begun || finished || run is null || files is null) { refused = true; return false; }
        try
        {
            run.AssertPinnedRun();
            if (run.RunId != frozenRunId) { refused = true; return false; }
            byte[] bytes = file.CopyBytes();
            bool report = file.Name == "capture-result.json";
            string expected = $"sample-{slots / 3:00}" + (slots % 3 == 0 ? ".reply.bin" : slots % 3 == 1 ? ".payload.bin" : ".json");
            bool valid = report ? slots % 3 == 0 && bytes.Length is > 0 and <= 32768
                : slots < 9 && file.Name == expected && (slots % 3 == 0 ? bytes.Length == 0xD54
                    : slots % 3 == 1 ? bytes.Length == 0xD50 : bytes.Length is > 0 and <= 8192);
            if (!valid || slots >= 10 || bytes.Length > MaximumBytes - bytesReserved
                || file.Sha256 != Convert.ToHexString(SHA256.HashData(bytes))) { refused = true; return false; }
            // Failed partials still consume this slot and its entire reserved bytes.
            slots++; bytesReserved += bytes.Length;
            files.StoreImmutable(file.Name, bytes, file.Sha256);
            if (run.RunId != frozenRunId) { refused = true; return false; }
            finished = report;
            return true;
        }
        catch (Exception) { refused = true; return false; }
    }

    internal void ReleaseAfterWorkerTerminal()
    {
        if (released) { return; }
        if (run is null || files is null) { return; }
        if (run.RunId != frozenRunId) { refused = true; throw new InvalidOperationException("PM run identity changed."); }
        run.AssertWorkerTerminal();
        if (run.RunId != frozenRunId) { refused = true; throw new InvalidOperationException("PM run identity changed."); }
        files.ReleaseAfterWorkerTerminal();
        released = true;
        refused = true;
    }
}

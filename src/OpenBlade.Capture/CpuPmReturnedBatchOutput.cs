using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Provenance for evidence files only. No value here grants invocation authority.
internal sealed record CpuPmCaptureProvenance(Guid RunId, Guid DriverLifetimeId, string SourceRevision,
    string PayloadManifestSha256, long MonotonicFrequency);
internal enum CpuPmCaptureWriteStatus { StoredComplete, StoredIncomplete, InvalidBatch, OutputUnavailable, WriteFailed }
internal sealed record CpuPmCaptureWriteResult(CpuPmCaptureWriteStatus Status, int VerifiedSamples, int VerifiedFiles);

internal sealed class CpuPmEvidenceFile
{
    private readonly byte[] bytes;
    internal CpuPmEvidenceFile(string name, byte[] content) { Name = name; bytes = (byte[])content.Clone(); Sha256 = Convert.ToHexString(SHA256.HashData(bytes)); }
    internal string Name { get; }
    internal string Sha256 { get; }
    internal byte[] CopyBytes() => (byte[])bytes.Clone();
}

// Future implementation must own a fresh create-new marker and deny-write/delete
// pins for the fixed private root/run ancestry. It must reject redirection or prior
// output, flush each file, atomically finalize without overwrite, and read back
// the exact bytes/hash before returning true. No production implementation exists.
internal interface ICpuPmCaptureEvidenceSink
{
    bool BeginFreshRun(Guid runId);
    bool TryWriteImmutable(CpuPmEvidenceFile file);
}

internal sealed class CpuPmReturnedBatchEvidence
{
    internal CpuPmReturnedBatchEvidence(Guid runId, bool complete, IEnumerable<CpuPmEvidenceFile> files)
    { RunId = runId; Complete = complete; Files = Array.AsReadOnly(files.ToArray()); }
    internal Guid RunId { get; }
    internal bool Complete { get; }
    internal IReadOnlyList<CpuPmEvidenceFile> Files { get; }
}

// Hardware-free returned-batch transformation. Nothing invokes or retries an
// adapter. Samples held by a hung process before batch return are unavailable.
internal static class CpuPmReturnedBatchOutput
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static CpuPmReturnedBatchEvidence? Prepare(CpuPmCaptureProvenance? provenance,
        CpuPmObservationBatch? batch, bool cancellationRequested)
    {
        if (provenance is null || batch is null || provenance.RunId == Guid.Empty || provenance.DriverLifetimeId == Guid.Empty
            || !Hex(provenance.SourceRevision, 40, upper: false) || !Hex(provenance.PayloadManifestSha256, 64, upper: true)
            || provenance.MonotonicFrequency <= 0) { return null; }
        CpuPmCompletedSample[] samples = batch.CopySamples();
        if (samples.Length > 3 || batch.Complete && (samples.Length != 3 || batch.Diagnostic is not null)
            || samples.Length > 0 && (batch.OrientationMask != 0xFF55 || batch.FuseMask != 0xFF55)) { return null; }
        var capEvidence = batch.FusedCap;
        if (capEvidence is not null && (!ValidCapRead(capEvidence.A0F, CpuLivePowerFusedCap.RegisterA0F)
            || !ValidCapRead(capEvidence.A0B, CpuLivePowerFusedCap.RegisterA0B)
            || capEvidence.A0B is not null && (capEvidence.A0F is null
                || capEvidence.A0B.MonotonicTimestamp < capEvidence.A0F.MonotonicTimestamp))
            || batch.Complete && capEvidence?.Candidate is null) { return null; }
        var files = new List<CpuPmEvidenceFile>();
        var summaries = new List<object>();
        long? previous = null;
        for (int index = 0; index < samples.Length; index++)
        {
            CpuPmCompletedSample? sample = samples[index];
            if (sample?.Snapshot is not { } snapshot || snapshot.CapturedUtc == default || snapshot.MonotonicTimestamp < 0
                || previous.HasValue && (snapshot.MonotonicTimestamp < previous.Value
                    || snapshot.MonotonicTimestamp - previous.Value < provenance.MonotonicFrequency)) { return null; }
            previous = snapshot.MonotonicTimestamp;
            byte[] reply = snapshot.CopyReply(), payload = snapshot.CopyPayload();
            if (reply.Length != 0xD54 || payload.Length != 0xD50 || !reply.AsSpan(4).SequenceEqual(payload)
                || !CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), snapshot.CapturedUtc, snapshot.MonotonicTimestamp, out var checkedSnapshot)
                || checkedSnapshot!.ReplySha256 != snapshot.ReplySha256 || checkedSnapshot.PayloadSha256 != snapshot.PayloadSha256
                || batch.Complete && snapshot.Validity != CpuPmRawSnapshotValidity.ValidCandidateObservation) { return null; }
            string prefix = $"sample-{index:00}";
            files.Add(new(prefix + ".reply.bin", reply)); files.Add(new(prefix + ".payload.bin", payload));
            var candidates = snapshot.CopyCandidates().Select(word => new
            {
                offset = word.Offset,
                rawBits = word.RawBits.ToString("X8"),
                value = float.IsFinite(word.Value) ? (float?)word.Value : null,
                classification = float.IsNaN(word.Value) ? "NaN" : float.IsPositiveInfinity(word.Value) ? "PositiveInfinity"
                    : float.IsNegativeInfinity(word.Value) ? "NegativeInfinity" : "Finite",
            }).ToArray();
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                provenance,
                index,
                snapshot.CapturedUtc,
                snapshot.MonotonicTimestamp,
                pmVersion = "005D000B",
                versionEvidence = "ProducerReportedEnginePreAndPost",
                replyLength = reply.Length,
                payloadLength = payload.Length,
                snapshot.ReplySha256,
                snapshot.PayloadSha256,
                refreshReturnedWord = sample.RefreshReturnedWord.ToString("X8"),
                validity = snapshot.Validity.ToString(),
                candidates,
                candidateMeaningsUnvalidated = true,
                livePptTarget = "Unknown",
                independentRestore = "Unproven",
            }, Json);
            if (metadata.Length > 8192) { return null; }
            var metadataFile = new CpuPmEvidenceFile(prefix + ".json", metadata); files.Add(metadataFile);
            summaries.Add(new { index, snapshot.ReplySha256, snapshot.PayloadSha256, metadataSha256 = metadataFile.Sha256 });
        }
        bool complete = batch.Complete && !cancellationRequested;
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            provenance,
            complete,
            adapterBatchComplete = batch.Complete,
            cancellationRequested,
            retainedSamples = samples.Length,
            samples = summaries,
            orientationMask = batch.OrientationMask.ToString("X8"),
            fuseMask = batch.FuseMask.ToString("X8"),
            fusedCap = batch.FusedCap is { } cap ? new
            {
                a0f = CapRead(cap.A0F),
                a0b = CapRead(cap.A0B),
                candidateWatts = cap.Candidate?.Watts,
                classification = "SourceMappedCandidateOnly",
                authority = "NotEstablishedByObservation",
            } : null,
            diagnostic = batch.Diagnostic is { } diagnostic ? new { stage = diagnostic.Stage.ToString(), failure = diagnostic.Failure.ToString() } : null,
            persistence = "ReturnedBatchOnly",
            samplesBeforeUnreturnedBatch = "Unavailable",
            parentAuthority = "NotEstablishedByOutputWriter",
            savedConfiguration = "NotQueried",
            livePptTarget = "Unknown",
            independentRestore = "Unproven",
            candidateMeaningsUnvalidated = true,
        }, Json);
        if (report.Length > 32768) { return null; }
        files.Add(new("capture-result.json", report));
        return new(provenance.RunId, complete, files);
    }

    internal static CpuPmCaptureWriteResult Write(CpuPmReturnedBatchEvidence? evidence, ICpuPmCaptureEvidenceSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (evidence is null) { return new(CpuPmCaptureWriteStatus.InvalidBatch, 0, 0); }
        int files = 0, samples = 0;
        try
        {
            if (!sink.BeginFreshRun(evidence.RunId)) { return new(CpuPmCaptureWriteStatus.OutputUnavailable, 0, 0); }
            // Do not recheck cancellation here: preserve the already-returned
            // bounded batch, including when its result follows cancellation.
            foreach (CpuPmEvidenceFile file in evidence.Files)
            {
                if (!sink.TryWriteImmutable(file)) { return new(CpuPmCaptureWriteStatus.WriteFailed, samples, files); }
                files++;
                if (file.Name.StartsWith("sample-", StringComparison.Ordinal) && file.Name.EndsWith(".json", StringComparison.Ordinal)) { samples++; }
            }
            return new(evidence.Complete ? CpuPmCaptureWriteStatus.StoredComplete : CpuPmCaptureWriteStatus.StoredIncomplete, samples, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { return new(CpuPmCaptureWriteStatus.WriteFailed, samples, files); }
    }

    private static bool Hex(string? value, int length, bool upper) => value is not null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' || (upper ? c is >= 'A' and <= 'F' : c is >= 'a' and <= 'f'));
    private static object? CapRead(CpuPmCapRegisterRead? read) => read is null ? null : new
    {
        address = read.Address.ToString("X8"),
        rawValue = read.RawValue.ToString("X8"),
        read.CapturedUtc,
        read.MonotonicTimestamp,
        read.TransportSucceeded,
        read.PostReadAdmissionPassed,
    };
    private static bool ValidCapRead(CpuPmCapRegisterRead? read, uint address) => read is null
        || read.Address == address && read.CapturedUtc != default && read.MonotonicTimestamp >= 0
            && (!read.PostReadAdmissionPassed || read.TransportSucceeded);
}

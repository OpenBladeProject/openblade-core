using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmReturnedBatchOutputTests
{
    private static CpuPmCaptureProvenance Provenance() => new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 40), new string('B', 64), 10_000_000);

    [Fact]
    public void CompleteBatchHasExactImmutableRawFilesHashesAndBoundedMetadata()
    {
        var batch = Batch(3, true); var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance(), batch, false)!;
        var sink = new Sink(); var written = CpuPmReturnedBatchOutput.Write(evidence, sink);
        Assert.Equal(CpuPmCaptureWriteStatus.StoredComplete, written.Status); Assert.Equal(3, written.VerifiedSamples); Assert.Equal(10, written.VerifiedFiles);
        for (int index = 0; index < 3; index++)
        {
            string prefix = $"sample-{index:00}";
            byte[] reply = sink.Files[prefix + ".reply.bin"], payload = sink.Files[prefix + ".payload.bin"];
            Assert.Equal(0xD54, reply.Length); Assert.Equal(0xD50, payload.Length); Assert.Equal(payload, reply[4..]);
            using var json = JsonDocument.Parse(sink.Files[prefix + ".json"]);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(reply)), json.RootElement.GetProperty("replySha256").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), json.RootElement.GetProperty("payloadSha256").GetString());
            Assert.Equal("Unknown", json.RootElement.GetProperty("livePptTarget").GetString());
            Assert.Equal(6, json.RootElement.GetProperty("candidates").GetArrayLength());
            Assert.False(json.RootElement.TryGetProperty("sessionId", out _));
        }
        using var report = JsonDocument.Parse(sink.Files["capture-result.json"]);
        Assert.True(report.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("ReturnedBatchOnly", report.RootElement.GetProperty("persistence").GetString());
        Assert.Equal("NotEstablishedByOutputWriter", report.RootElement.GetProperty("parentAuthority").GetString());
        byte[] mutable = evidence.Files[0].CopyBytes(); mutable[0] = 0;
        Assert.Equal(0x50, evidence.Files[0].CopyBytes()[0]);
        Assert.Equal("capture-result.json", sink.Order.Last());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialFailurePersistsEveryReturnedSampleAndNeverClaimsCompletion(int count)
    {
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance(), Batch(count, false), false);
        var sink = new Sink(); var result = CpuPmReturnedBatchOutput.Write(evidence, sink);
        Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status); Assert.Equal(count, result.VerifiedSamples);
        using var report = JsonDocument.Parse(sink.Files["capture-result.json"]);
        Assert.False(report.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(count, report.RootElement.GetProperty("retainedSamples").GetInt32());
        Assert.Equal("NativeFailure", report.RootElement.GetProperty("diagnostic").GetProperty("failure").GetString());
    }

    [Fact]
    public void CancellationAfterReturnPreservesAllEvidenceWithIncompleteStatus()
    {
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance(), Batch(3, true), true);
        var sink = new Sink(); var result = CpuPmReturnedBatchOutput.Write(evidence, sink);
        Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status); Assert.Equal(3, result.VerifiedSamples);
        using var report = JsonDocument.Parse(sink.Files["capture-result.json"]);
        Assert.True(report.RootElement.GetProperty("cancellationRequested").GetBoolean());
        Assert.True(report.RootElement.GetProperty("adapterBatchComplete").GetBoolean());
    }

    [Theory]
    [InlineData(0x7FC00001u, "NaN")]
    [InlineData(0x7F800000u, "PositiveInfinity")]
    [InlineData(0xFF800000u, "NegativeInfinity")]
    public void NonfiniteCandidateRetainsExactBitsAndRawBytesWithoutInvalidJson(uint bits, string classification)
    {
        var sample = Sample(0, bits); var batch = new CpuPmObservationBatch(false, [sample], [], 0xFF55, 0xFF55,
            new(CpuPmObservationStage.Copy, CpuPmObservationFailure.InvalidData));
        var sink = new Sink(); var result = CpuPmReturnedBatchOutput.Write(CpuPmReturnedBatchOutput.Prepare(Provenance(), batch, false), sink);
        Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status);
        using var json = JsonDocument.Parse(sink.Files["sample-00.json"]);
        var candidate = json.RootElement.GetProperty("candidates")[0];
        Assert.Equal(bits.ToString("X8"), candidate.GetProperty("rawBits").GetString());
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("value").ValueKind);
        Assert.Equal(classification, candidate.GetProperty("classification").GetString());
        Assert.Equal(bits, BinaryPrimitives.ReadUInt32LittleEndian(sink.Files["sample-00.payload.bin"]));
    }

    [Fact]
    public void MalformedProvenanceOrBatchNeverBeginsOutput()
    {
        var good = Provenance(); var sink = new Sink();
        CpuPmCaptureProvenance?[] invalid = [null, good with { RunId = Guid.Empty }, good with { DriverLifetimeId = Guid.Empty },
            good with { SourceRevision = "private/path" }, good with { PayloadManifestSha256 = new string('b', 64) }, good with { MonotonicFrequency = 0 }];
        foreach (var context in invalid)
        {
            Assert.Null(CpuPmReturnedBatchOutput.Prepare(context, Batch(3, true), false));
            Assert.Equal(CpuPmCaptureWriteStatus.InvalidBatch, CpuPmReturnedBatchOutput.Write(null, sink).Status);
        }
        Assert.Equal(0, sink.Begins);
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(good, Batch(2, true), false));
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(good, Batch(4, false), false));
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(good, new(false, [Sample(1), Sample(0)], [], 0xFF55, 0xFF55, null), false));
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(good, new(false, [Sample(0)], [], 0, 0, null), false));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(10, 3)]
    public void WriteOrVerificationFailureStopsWithoutRetryAndRetainsPriorImmutableFiles(int fail, int samples)
    {
        var sink = new Sink { FailAt = fail }; var result = CpuPmReturnedBatchOutput.Write(
            CpuPmReturnedBatchOutput.Prepare(Provenance(), Batch(3, true), false), sink);
        Assert.Equal(CpuPmCaptureWriteStatus.WriteFailed, result.Status); Assert.Equal(samples, result.VerifiedSamples);
        Assert.Equal(fail, sink.Attempts); Assert.Equal(fail - 1, result.VerifiedFiles);
        Assert.False(sink.Files.ContainsKey("capture-result.json"));
    }

    [Fact]
    public void ExistingRunCannotBeOverwrittenAndSinkExceptionDoesNotLeakText()
    {
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance(), Batch(1, false), false); var sink = new Sink();
        Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, CpuPmReturnedBatchOutput.Write(evidence, sink).Status);
        var before = sink.Files.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)));
        Assert.Equal(CpuPmCaptureWriteStatus.OutputUnavailable, CpuPmReturnedBatchOutput.Write(evidence, sink).Status);
        Assert.All(sink.Files, pair => Assert.Equal(before[pair.Key], Convert.ToHexString(SHA256.HashData(pair.Value))));
        var throwing = new Sink { ThrowAt = 2 };
        var result = CpuPmReturnedBatchOutput.Write(evidence, throwing);
        Assert.Equal(CpuPmCaptureWriteStatus.WriteFailed, result.Status); Assert.DoesNotContain("private", result.ToString());
    }

    private static CpuPmObservationBatch Batch(int count, bool complete) => new(complete,
        Enumerable.Range(0, count).Select(i => Sample(i)), [], count == 0 ? 0 : 0xFF55u, count == 0 ? 0 : 0xFF55u,
        complete ? null : new(CpuPmObservationStage.Copy, CpuPmObservationFailure.NativeFailure), Cap());
    private static CpuPmFusedCapObservation Cap() => new(
        new(0x03820A0F, 5, DateTimeOffset.UnixEpoch, 0, true, true),
        new(0x03820A0B, 0xF0000000, DateTimeOffset.UnixEpoch, 0, true, true));

    [Fact]
    public void CompleteCandidateAndRawRegisterStampsAreWrittenWithoutAuthorityClaim()
    {
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance(), Batch(3, true), false)!;
        Assert.Equal(10, evidence.Files.Count);
        using var json = JsonDocument.Parse(evidence.Files.Last().CopyBytes());
        var cap = json.RootElement.GetProperty("fusedCap");
        Assert.Equal(95u, cap.GetProperty("candidateWatts").GetUInt32());
        Assert.Equal("NotEstablishedByObservation", cap.GetProperty("authority").GetString());
        Assert.Equal("03820A0F", cap.GetProperty("a0f").GetProperty("address").GetString());
        Assert.Equal("00000005", cap.GetProperty("a0f").GetProperty("rawValue").GetString());
        Assert.Equal("F0000000", cap.GetProperty("a0b").GetProperty("rawValue").GetString());
        Assert.Equal(DateTimeOffset.UnixEpoch, cap.GetProperty("a0b").GetProperty("capturedUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialOrRejectedCapReplyPersistsWithNoCandidate(bool secondReturned)
    {
        var cap = new CpuPmFusedCapObservation(Cap().A0F,
            secondReturned ? Cap().A0B! with { RawValue = uint.MaxValue, PostReadAdmissionPassed = false } : null);
        var batch = new CpuPmObservationBatch(false, [], [], 0xFF55, 0xFF55,
            new(CpuPmObservationStage.FusedCap, CpuPmObservationFailure.ReplyRejected), cap);
        var sink = new Sink(); var result = CpuPmReturnedBatchOutput.Write(CpuPmReturnedBatchOutput.Prepare(Provenance(), batch, true), sink);
        Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status); Assert.Equal(1, result.VerifiedFiles);
        using var json = JsonDocument.Parse(sink.Files["capture-result.json"]);
        var saved = json.RootElement.GetProperty("fusedCap");
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("candidateWatts").ValueKind);
        if (secondReturned) { Assert.Equal("FFFFFFFF", saved.GetProperty("a0b").GetProperty("rawValue").GetString()); }
        else { Assert.Equal(JsonValueKind.Null, saved.GetProperty("a0b").ValueKind); }
        Assert.Equal("FusedCap", json.RootElement.GetProperty("diagnostic").GetProperty("stage").GetString());
    }

    [Fact]
    public void InconsistentCapMetadataCannotClaimCompleteOutput()
    {
        var valid = Cap();
        CpuPmFusedCapObservation?[] bad = [null, new(valid.A0F, null), new(null, valid.A0B),
            new(valid.A0F! with { Address = 0x123 }, valid.A0B),
            new(valid.A0F! with { CapturedUtc = default }, valid.A0B),
            new(valid.A0F! with { MonotonicTimestamp = 1 }, valid.A0B),
            new(valid.A0F! with { TransportSucceeded = false }, valid.A0B)];
        foreach (var cap in bad)
        {
            var batch = new CpuPmObservationBatch(true, Batch(3, true).CopySamples(), [], 0xFF55, 0xFF55, null, cap);
            Assert.Null(CpuPmReturnedBatchOutput.Prepare(Provenance(), batch, false));
        }
    }
    private static CpuPmCompletedSample Sample(int index, uint bits = 0x3F800000)
    {
        byte[] reply = new byte[0xD54]; BinaryPrimitives.WriteUInt32LittleEndian(reply, 0xD50);
        for (int word = 0; word < 6; word++) { BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4 + 4 * word), word == 0 ? bits : 0x3F800000); }
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), DateTimeOffset.UnixEpoch.AddSeconds(index), index * 10_000_000L, out var snapshot));
        return new(snapshot!, 0x12345678);
    }
    // Contract fake only. No filesystem/provider implementation can grant runtime
    // output or parent authority in this slice.
    private sealed class Sink : ICpuPmCaptureEvidenceSink
    {
        internal int Begins, Attempts, FailAt, ThrowAt;
        internal Dictionary<string, byte[]> Files = [];
        internal List<string> Order = [];
        private bool started;
        public bool BeginFreshRun(Guid runId) { Begins++; if (started) { return false; } started = true; return true; }
        public bool TryWriteImmutable(CpuPmEvidenceFile file)
        {
            Attempts++; if (Attempts == ThrowAt) { throw new IOException("private/path"); }
            if (Attempts == FailAt) { return false; }
            byte[] bytes = file.CopyBytes(); Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
            if (!Files.TryAdd(file.Name, bytes)) { return false; }
            Order.Add(file.Name); return true;
        }
    }
}

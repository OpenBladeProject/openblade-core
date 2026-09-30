using System.Buffers.Binary;
using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmRangeOutputTests
{
    private static readonly CpuPmCaptureProvenance Provenance = new(Guid.NewGuid(), Guid.NewGuid(), new('a', 40), new('A', 64), 10_000_000);
    private const CpuLivePowerRangeMetadataStages Stages = CpuLivePowerRangeMetadataStages.InvocationEntered
        | CpuLivePowerRangeMetadataStages.InvocationReturned | CpuLivePowerRangeMetadataStages.OutputEnumerated
        | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed;
    private static CpuPmCapRegisterRead Read(uint address, long time) => new(address, 5, DateTimeOffset.UnixEpoch.AddSeconds(time), time, true, true);
    private static CpuLivePowerRangeMetadataCall<T> Call<T>(T value, long time) where T : class =>
        new(value, CpuLivePowerRangeMetadataStatus.Complete, Stages, DateTimeOffset.UnixEpoch.AddSeconds(time), time, PostAdmissionPassed: true);
    private static CpuLivePowerRangeObservation Range() => new(Read(0x03820A0F, 0), Read(0x03820A0B, 1),
        Read(0x03820A13, 2), Read(0x03820A90, 3), new(0x82, 0, true, true, 2, DateTimeOffset.UnixEpoch.AddSeconds(4), 4, true, true),
        Call(new CpuLivePowerRangeVersion(7, true), 6), null,
        Call(new CpuLivePowerRangeMinMax(0x50001, 0, 0, 125000, 1), 8),
        CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001);
    private static CpuPmObservationBatch Batch(CpuLivePowerRangeObservation? range, bool complete = true,
        CpuPmObservationProfile profile = CpuPmObservationProfile.RangeSingleSample)
    {
        byte[] reply = new byte[0xD54]; BinaryPrimitives.WriteUInt32LittleEndian(reply, 0xD50);
        for (int index = 0; index < 6; index++) { BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4 + index * 4), 0x42AA0000); }
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), DateTimeOffset.UnixEpoch.AddSeconds(5), 5, out var sample));
        return new(complete, [new(sample!, 0)], [], 0xFF55, 0xFF55,
            complete ? null : new(CpuPmObservationStage.Admission, CpuPmObservationFailure.InvalidData),
            new(range?.A0F, range?.A0B), profile, range);
    }

    [Fact]
    public void RangeWritesOneExactSampleAndBoundedTypedMetadataWithoutAuthority()
    {
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(Range()), false)!;
        Assert.True(evidence.Complete); Assert.Equal(4, evidence.Files.Count);
        using var json = JsonDocument.Parse(evidence.Files.Last().CopyBytes());
        var root = json.RootElement;
        Assert.Equal("RangeSingleSample", root.GetProperty("profile").GetString());
        Assert.Equal(1, root.GetProperty("retainedSamples").GetInt32());
        var range = root.GetProperty("rangeObservation");
        Assert.Equal("VendorIntervalEvidenceOnly", range.GetProperty("classification").GetString());
        Assert.Equal("NotEstablishedByObservation", range.GetProperty("authority").GetString());
        Assert.Equal(125000u, range.GetProperty("minMax").GetProperty("value").GetProperty("max").GetUInt32());
        Assert.Equal(JsonValueKind.Null, range.GetProperty("dValues").ValueKind);
        Assert.Equal("SourceMappedContinuousTypeOnePpt50001", range.GetProperty("metadataApplicability").GetString());
    }

    [Fact]
    public void MetadataFailureRetainsPmAndCopiedValueAsIncompleteEvidence()
    {
        var range = Range();
        range = range with { MinMax = range.MinMax! with { Status = CpuLivePowerRangeMetadataStatus.DisposalFailed, PostAdmissionPassed = false } };
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(range), false));
        var evidence = CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(range, false), true)!;
        Assert.False(evidence.Complete); Assert.Equal(4, evidence.Files.Count);
        using var json = JsonDocument.Parse(evidence.Files.Last().CopyBytes());
        Assert.Equal("DisposalFailed", json.RootElement.GetProperty("rangeObservation").GetProperty("minMax").GetProperty("status").GetString());
        Assert.Equal(125000u, json.RootElement.GetProperty("rangeObservation").GetProperty("minMax").GetProperty("value").GetProperty("max").GetUInt32());
    }

    [Fact]
    public void LegacyAndIncompleteScopeCannotClaimRangeCompletion()
    {
        var range = Range();
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(range, false, CpuPmObservationProfile.IdleThreeSamples), false));
        Assert.Null(CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(null), false));
        foreach (var bad in new[] { range with { A90 = null }, range with { A13 = range.A13! with { Address = 0x123 } },
            range with { A13 = range.A13! with { MonotonicTimestamp = 0 } }, range with { Capability = range.Capability! with { Argument = 1 } },
            range with { QueryVersion = range.QueryVersion! with { Value = new(8, true) } },
            range with { MetadataApplicability = CpuLivePowerRangeMetadataApplicability.Unknown },
            range with { MetadataApplicability = (CpuLivePowerRangeMetadataApplicability)99 },
            range with { DValues = Call(new CpuLivePowerRangeDValues(0x50001, 0, 2, [45000, 95000]), 7) },
            range with { MinMax = range.MinMax! with { PostAdmissionPassed = false } } })
        { Assert.Null(CpuPmReturnedBatchOutput.Prepare(Provenance, Batch(bad), false)); }
    }
}

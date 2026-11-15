using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLivePowerPlanTests
{
    [Theory]
    [InlineData(85000u, 0x42AA0001u)]
    [InlineData(80000u, 0x42A00000u)]
    [InlineData(79000u, 0x429E0000u)]
    public void ArithmeticFixturesReconstructExactModelBitsWithoutGrantingAuthority(uint milliwatts, uint bits)
    {
        Assert.True(CpuLivePowerValue.TryFromRawBits(bits, out var observed));
        Assert.Equal(milliwatts, observed.Milliwatts);
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(milliwatts, out var encoded));
        Assert.Equal(bits, encoded.RawBits);
        Assert.Equal(observed, encoded);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(79999u)]
    [InlineData(85001u)]
    [InlineData(100123u)]
    [InlineData(1023000u)]
    public void IntegerMilliwattsNeedNotBeWholeWatts(uint milliwatts)
    {
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(milliwatts, out var encoded));
        Assert.True(CpuLivePowerValue.TryFromRawBits(encoded.RawBits, out var decoded));
        Assert.Equal(milliwatts, decoded.Milliwatts);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    [InlineData(0xBF800000u)]
    [InlineData(0x7F800000u)]
    [InlineData(0xFF800000u)]
    [InlineData(0x7FC00001u)]
    [InlineData(0x42AA0000u)] // Exact 85f is not the integer-mW multiplication result.
    [InlineData(0x4B800000u)] // Outside the bounded unique inverse domain.
    public void NonfiniteNonpositiveNonmatchingAndOutOfDomainBitsAreRejected(uint bits)
    { Assert.False(CpuLivePowerValue.TryFromRawBits(bits, out _)); }

    [Fact]
    public void SnapshotUsesExactOffsetsAndDefensivelyPreservesWholePayload()
    {
        byte[] reply = Reply(); reply[4 + 0x7BC] = 0xA5;
        var raw = Raw(reply); var snapshot = CpuLivePowerSnapshot.TryCreate(raw)!;
        Assert.Equal(85000u, snapshot.Pair.Fast.Milliwatts); Assert.Equal(80000u, snapshot.Pair.Slow.Milliwatts);
        Assert.Equal(Bits(60000), snapshot.Pair.StapmRawBits);
        Assert.Equal(raw.PayloadSha256, snapshot.PayloadSha256);
        Assert.Equal(raw.CapturedUtc, snapshot.CapturedUtc); Assert.Equal(123, snapshot.MonotonicTimestamp);
        reply[12] = 0; byte[] copy = snapshot.CopyOriginalPayload(); copy[0x7BC] = 0;
        Assert.Equal(0xA5, snapshot.CopyOriginalPayload()[0x7BC]);
        Assert.Equal(0x42AA0001u, snapshot.Pair.Fast.RawBits);
    }

    [Fact]
    public void SnapshotRejectsWrongEnvelopeVersionTimestampAndCandidateShape()
    {
        Assert.Null(CpuLivePowerSnapshot.TryCreate(null));
        Assert.False(CpuPmRawSnapshot.TryCreate(0x005D000A, new(true, Reply()), DateTimeOffset.UtcNow, 1, out _));
        Assert.False(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, new byte[0xD50]), DateTimeOffset.UtcNow, 1, out _));
        var badHeader = Reply(); Put(badHeader, 0, 0xD4C);
        Assert.False(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, badHeader), DateTimeOffset.UtcNow, 1, out _));
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, Reply()), default, 1, out var undated));
        Assert.Null(CpuLivePowerSnapshot.TryCreate(undated));
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, Reply()), DateTimeOffset.UtcNow, -1, out var negativeClock));
        Assert.Null(CpuLivePowerSnapshot.TryCreate(negativeClock));
        var nonfinite = Reply(); Put(nonfinite, 4 + 12, 0x7FC00000);
        Assert.Null(CpuLivePowerSnapshot.TryCreate(Raw(nonfinite)));
        var unmatched = Reply(); Put(unmatched, 4 + 8, 0x42AA0000);
        Assert.Null(CpuLivePowerSnapshot.TryCreate(Raw(unmatched)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhoenixPeerRetainsExactThreeLimitsAndRejectsCrossModelOrCrossLayout(bool blade2023)
    {
        var phoenix = blade2023 ? CpuLiveCurveContract.Rz090482 : CpuLiveCurveContract.Rz090508;
        uint fastBits = BitConverter.SingleToUInt32Bits(54f), slowBits = BitConverter.SingleToUInt32Bits(45f);
        byte[] reply = new byte[0xAF4];
        Put(reply, 0, 0xAF0);
        Put(reply, 12, fastBits);
        Put(reply, 20, slowBits);
        Assert.True(CpuPmRawSnapshot.TryCreate(phoenix, 0x004C0008, new(true, reply), DateTimeOffset.UnixEpoch, 1, out var raw));
        var snapshot = CpuLivePowerSnapshot.TryCreate(phoenix, raw);
        Assert.NotNull(snapshot);
        Assert.Same(phoenix, snapshot.Contract);
        var pair = snapshot.Pair;
        Assert.Equal(0u, pair.StapmRawBits);
        Assert.Equal(fastBits, pair.Fast.RawBits);
        Assert.Equal(slowBits, pair.Slow.RawBits);
        Assert.Equal(54000u, pair.Fast.Milliwatts);
        Assert.Equal(45000u, pair.Slow.Milliwatts);
        Assert.Null(CpuLivePowerSnapshot.TryCreate(raw));
        Assert.Null(CpuLivePowerSnapshot.TryCreate(phoenix, Raw(Reply())));
        Assert.False(CpuPmRawSnapshot.TryCreate(phoenix, 0x004C0009, new(true, reply), DateTimeOffset.UnixEpoch, 1, out _));
        Assert.False(CpuPmRawSnapshot.TryCreate(phoenix, 0x004C0008, new(true, Reply()), DateTimeOffset.UnixEpoch, 1, out _));

        foreach (var changed in new[]
        {
            CpuLivePowerPair.DecodeCurvePeer(phoenix, 0x80000000, fastBits, slowBits),
            CpuLivePowerPair.DecodeCurvePeer(phoenix, 0, fastBits + 1, slowBits),
            CpuLivePowerPair.DecodeCurvePeer(phoenix, 0, fastBits, slowBits + 1),
        })
        {
            Assert.NotNull(changed);
            Assert.Equal(pair.Fast.Milliwatts, changed.Fast.Milliwatts);
            Assert.Equal(pair.Slow.Milliwatts, changed.Slow.Milliwatts);
            Assert.NotEqual(pair, changed);
        }
        Assert.Null(CpuLivePowerPair.DecodeCurvePeer(CpuLiveCurveContract.Rz090528, 0, fastBits, slowBits));
    }

    [Fact]
    public void PhoenixPeerPreservesFiniteWattsBeyondDisplayRangeAndRejectsMalformedReadings()
    {
        var contract = CpuLiveCurveContract.Rz090508;
        uint large = BitConverter.SingleToUInt32Bits(float.MaxValue), sixty = BitConverter.SingleToUInt32Bits(60f);
        var pair = CpuLivePowerPair.DecodeCurvePeer(contract, 0, large, sixty);
        Assert.NotNull(pair);
        Assert.Equal(large, pair.Fast.RawBits);
        Assert.Null(pair.Fast.Milliwatts);
        Assert.Equal(60000u, pair.Slow.Milliwatts);
        foreach (uint malformed in new[] { 0xBF800000u, 0x7FC00000u, 0x7F800000u })
        {
            Assert.Null(CpuLivePowerPair.DecodeCurvePeer(contract, malformed, sixty, sixty));
            Assert.Null(CpuLivePowerPair.DecodeCurvePeer(contract, 0, malformed, sixty));
            Assert.Null(CpuLivePowerPair.DecodeCurvePeer(contract, 0, sixty, malformed));
        }
    }

    private static uint Bits(uint milliwatts) => BitConverter.SingleToUInt32Bits((float)milliwatts * 0.001f);
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static byte[] Reply()
    {
        var reply = new byte[0xD54]; Put(reply, 0, 0xD50);
        Put(reply, 4, Bits(60000)); Put(reply, 12, Bits(85000)); Put(reply, 20, Bits(80000));
        return reply;
    }
    private static CpuPmRawSnapshot Raw(byte[] reply)
    {
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 123, out var raw));
        return raw!;
    }
}

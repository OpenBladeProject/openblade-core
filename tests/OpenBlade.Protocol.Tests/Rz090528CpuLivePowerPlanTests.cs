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
    public void CodecOnlyEncodesIndependentFastAndSlowMessages()
    {
        Assert.True(CpuLivePowerCommand.TryCreate(CpuLivePowerLimit.Fast, 85000, out var fast));
        Assert.True(CpuLivePowerCommand.TryCreate(CpuLivePowerLimit.Slow, 80000, out var slow));
        Assert.Equal(0x32u, fast.Message); Assert.Equal(0x33u, slow.Message);
        Assert.Equal(85000u, fast.EncodeArgument()); Assert.Equal(80000u, slow.EncodeArgument());
        Assert.True(CpuLivePowerCommand.TryDecode(0x33, 80000, out var decoded)); Assert.Equal(slow, decoded);
        foreach (uint message in new uint[] { 0, 6, 0x31, 0x53, 0x65, uint.MaxValue })
        { Assert.False(CpuLivePowerCommand.TryDecode(message, 80000, out _)); }
        foreach (uint value in new uint[] { 0, 1023001, uint.MaxValue })
        { Assert.False(CpuLivePowerCommand.TryDecode(0x32, value, out _)); }
        Assert.False(CpuLivePowerCommand.TryCreate((CpuLivePowerLimit)2, 80000, out _));
        Assert.Throws<InvalidOperationException>(() => default(CpuLivePowerCommand).EncodeArgument());
    }

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

    [Fact]
    public void SlowOnlyPlanRestoresItsOwnBaselineAndNeverFlattensFastOrStapm()
    {
        var admitted = new List<(uint, uint, CpuLivePowerCommandPurpose)>();
        var result = Plan(Raw(Reply()), [new(CpuLivePowerLimit.Slow, 79)], Cap(95),
            (command, purpose) => { admitted.Add((command.Message, command.Milliwatts, purpose)); return true; });
        Assert.Equal(CpuLivePowerPlanStatus.Prepared, result.Status);
        var step = Assert.Single(result.Plan!.Steps);
        Assert.Equal((0x33u, 79000u, CpuLivePowerCommandPurpose.Forward), admitted[0]);
        Assert.Equal((0x33u, 80000u, CpuLivePowerCommandPurpose.Restore), admitted[1]);
        Assert.Equal(step.Before.Fast, step.After.Fast); Assert.Equal(step.Before.StapmRawBits, step.After.StapmRawBits);
        Assert.Equal(step.Before, step.After.With(step.RestoreCommand));
        Assert.Equal(0x42AA0001u, result.Plan.Expected.Fast.RawBits);
    }

    [Fact]
    public void MixedFractionalBaselineAndReverseOrderRestoreEveryIndependentField()
    {
        var result = Plan(Raw(Reply(85001, 79999)),
            [new(CpuLivePowerLimit.Slow, 78), new(CpuLivePowerLimit.Fast, 84)], Cap(95), (_, _) => true);
        var plan = result.Plan!; Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(CpuLivePowerLimit.Fast, plan.Steps[0].Command.Limit);
        Assert.Equal(plan.Steps[0].After, plan.Steps[1].Before);
        var restored = plan.Expected;
        foreach (var step in plan.Steps.Reverse()) { restored = restored.With(step.RestoreCommand); Assert.Equal(step.Before, restored); }
        Assert.Equal(plan.Baseline.Pair, restored);
        Assert.Equal(85001u, restored.Fast.Milliwatts); Assert.Equal(79999u, restored.Slow.Milliwatts);
    }

    [Fact]
    public void RestorationOutsideEditorRangeRequiresItsOwnExplicitAdmission()
    {
        var baseline = Raw(Reply(100123, 80000)); bool restoreSeen = false;
        var denied = Plan(baseline, [new(CpuLivePowerLimit.Fast, 90)], Cap(110),
            (command, purpose) => { if (purpose == CpuLivePowerCommandPurpose.Restore) { restoreSeen = true; Assert.Equal(100123u, command.Milliwatts); return false; } return true; });
        Assert.True(restoreSeen); Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied, denied.Status); Assert.Null(denied.Plan);
        var admitted = Plan(baseline, [new(CpuLivePowerLimit.Fast, 90)], Cap(110), (_, _) => true);
        Assert.Equal(100123u, Assert.Single(admitted.Plan!.Steps).RestoreCommand.Milliwatts);
    }

    [Fact]
    public void LateAdmissionFailureOrExceptionReturnsNoPartialPlan()
    {
        foreach (bool throws in new[] { false, true })
        {
            int calls = 0;
            var result = Plan(Raw(Reply()),
                [new(CpuLivePowerLimit.Fast, 84), new(CpuLivePowerLimit.Slow, 79)], Cap(95), (_, _) =>
                { if (++calls != 4) { return true; } if (throws) { throw new FormatException("private exception"); } return false; });
            Assert.Equal(4, calls); Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied, result.Status); Assert.Null(result.Plan);
        }
        Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied,
            Plan(Raw(Reply()), [new(CpuLivePowerLimit.Fast, 84)], Cap(95), null).Status);
    }

    [Fact]
    public void TargetsAreFrozenBeforeAnyAdmissionCallback()
    {
        var targets = new List<CpuLivePowerTarget> { new(CpuLivePowerLimit.Fast, 84), new(CpuLivePowerLimit.Slow, 79) };
        var result = Plan(Raw(Reply()), targets, Cap(95), (_, _) => { targets.Clear(); return true; });
        Assert.Equal(2, result.Plan!.Steps.Count); Assert.Equal(79000u, result.Plan.Expected.Slow.Milliwatts);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLivePowerStep>)result.Plan.Steps).Clear());
    }

    [Fact]
    public void NoOpHasNoPlanAndDoesNotNeedCapOrCreateOwnership()
    {
        foreach (CpuLivePowerTarget[] targets in new CpuLivePowerTarget[][] { [], [new(CpuLivePowerLimit.Fast, 85), new(CpuLivePowerLimit.Slow, 80)] })
        {
            var result = Plan(Raw(Reply()), targets, null, (_, _) => throw new Exception());
            Assert.Equal(CpuLivePowerPlanStatus.Unchanged, result.Status); Assert.Null(result.Plan); Assert.NotNull(result.Baseline);
        }
    }

    [Theory]
    [InlineData(44)]
    [InlineData(96)]
    public void EditorRangeIsWholeWattsAndDeniedBeforeAdmission(int watts)
    {
        var result = Plan(Raw(Reply()), [new(CpuLivePowerLimit.Fast, watts)], Cap(95), (_, _) => throw new Exception());
        Assert.Equal(CpuLivePowerPlanStatus.InvalidTarget, result.Status); Assert.Null(result.Plan);
    }

    [Fact]
    public void DuplicateNullUnknownAndOversizedTargetsAreRejected()
    {
        CpuLivePowerTarget[][] cases = [ [new(CpuLivePowerLimit.Fast, 84), new(CpuLivePowerLimit.Fast, 83)],
            [null!], [new((CpuLivePowerLimit)9, 80)], [new(CpuLivePowerLimit.Fast, 84), new(CpuLivePowerLimit.Slow, 79), new(CpuLivePowerLimit.Fast, 83)] ];
        foreach (var targets in cases)
        { Assert.Equal(CpuLivePowerPlanStatus.InvalidTarget, Plan(Raw(Reply()), targets, Cap(95), (_, _) => throw new Exception()).Status); }
    }

    [Fact]
    public void CapIsDecodedFromTwoRegistersNotPayloadAndIsOnlyCandidateData()
    {
        var cap = CpuLivePowerFusedCap.TryDecode(0xA5000005, 0xF1234567)!;
        Assert.Equal(95u, cap.Watts); Assert.Equal(95000u, cap.Milliwatts);
        Assert.Equal(0xA5000005u, cap.RawA0F); Assert.Equal(0xF1234567u, cap.RawA0B);
        Assert.Equal(1u, Cap(1).Watts); Assert.Equal(1023u, Cap(1023).Watts);
        Assert.Null(CpuLivePowerFusedCap.TryDecode(0, 0));
        Assert.Null(CpuLivePowerFusedCap.TryDecode(uint.MaxValue, 0));
        Assert.Null(CpuLivePowerFusedCap.TryDecode(0, uint.MaxValue));
        foreach (var insufficient in new CpuLivePowerFusedCap?[] { null, Cap(78) })
        {
            var result = Plan(Raw(Reply()), [new(CpuLivePowerLimit.Slow, 79)], insufficient, (_, _) => throw new Exception());
            Assert.Equal(CpuLivePowerPlanStatus.InvalidCap, result.Status); Assert.Null(result.Plan);
        }
        // Correct cap bytes alone never supply even pure-plan admission.
        Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied,
            Plan(Raw(Reply()), [new(CpuLivePowerLimit.Slow, 79)], cap, null).Status);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(4u, false)]
    [InlineData(128u, false)]
    [InlineData(2u, true)]
    [InlineData(0xE6u, true)]
    public void OnlyCapabilityBitOneBypassesFusedCeilingForForwardAndRestore(uint rawCapability, bool bypasses)
    {
        // First case exceeds the cap only on restoration; second only on forward.
        foreach (var (baseline, target) in new[] { (85000u, 70), (70000u, 79) })
        {
            int calls = 0;
            var capability = CpuLivePowerCapability.TryDecode(rawCapability)!;
            Assert.Equal(rawCapability, capability.RawValue);
            Assert.Equal(!bypasses, capability.AppliesFusedCeiling);
            var result = Rz090528CpuLivePowerPlan.CreateSelective(Raw(Reply(baseline, 70000)),
                [new(CpuLivePowerLimit.Fast, target)], Cap(71), capability, Range(), (_, _) => { calls++; return true; });
            Assert.Equal(bypasses ? CpuLivePowerPlanStatus.Prepared : CpuLivePowerPlanStatus.InvalidCap, result.Status);
            Assert.Equal(bypasses ? 2 : 0, calls);
            Assert.Equal(bypasses, result.Plan is not null);
        }
    }

    [Fact]
    public void CapabilityBypassRetainsExactUnequalRestorationAndIndependentAdmission()
    {
        var capability = CpuLivePowerCapability.TryDecode(2)!; var range = Range();
        var commands = new List<(CpuLivePowerCommand, CpuLivePowerCommandPurpose)>();
        var baseline = Raw(Reply(100123, 80765));
        CpuLivePowerTarget[] targets = [new(CpuLivePowerLimit.Fast, 90), new(CpuLivePowerLimit.Slow, 80)];
        var result = Rz090528CpuLivePowerPlan.CreateSelective(baseline, targets, Cap(71), capability, range,
            (command, purpose) => { commands.Add((command, purpose)); return true; });
        Assert.Equal(CpuLivePowerPlanStatus.Prepared, result.Status);
        var plan = result.Plan!; Assert.Same(capability, plan.Capability); Assert.Same(range, plan.FirmwareRange);
        Assert.Equal(4, commands.Count);
        Assert.Equal((100123u, CpuLivePowerCommandPurpose.Restore), (commands[1].Item1.Milliwatts, commands[1].Item2));
        Assert.Equal((80765u, CpuLivePowerCommandPurpose.Restore), (commands[3].Item1.Milliwatts, commands[3].Item2));
        var restored = plan.Expected;
        foreach (var step in plan.Steps.Reverse()) { restored = restored.With(step.RestoreCommand); Assert.Equal(step.Before, restored); }
        Assert.Equal(plan.Baseline.Pair, restored);
        Assert.Equal(Bits(100123), restored.Fast.RawBits); Assert.Equal(Bits(80765), restored.Slow.RawBits);
        Assert.Equal(Bits(60000), restored.StapmRawBits);

        int calls = 0;
        var denied = Rz090528CpuLivePowerPlan.CreateSelective(baseline, targets, Cap(71), capability, range, (_, _) => ++calls < 4);
        Assert.Equal(4, calls); Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied, denied.Status); Assert.Null(denied.Plan);
        Assert.Equal(CpuLivePowerPlanStatus.AdmissionDenied,
            Rz090528CpuLivePowerPlan.CreateSelective(baseline, targets, Cap(71), capability, range, null).Status);
    }

    [Theory]
    [InlineData(85000u, 89000u)] // Forward 90W exceeds the firmware maximum.
    [InlineData(100123u, 100000u)] // Only the exact original restore exceeds it.
    [InlineData(45000u, 100000u)] // Only the exact original restore is below the minimum.
    public void FirmwareRangeStillChecksForwardAndRestorationWhenFusedCeilingIsBypassed(uint baseline, uint maximum)
    {
        int calls = 0;
        var result = Rz090528CpuLivePowerPlan.CreateSelective(Raw(Reply(baseline, 80000)),
            [new(CpuLivePowerLimit.Fast, 90)], Cap(71), CpuLivePowerCapability.TryDecode(2),
            new(0x50001, 50000, maximum, 1), (_, _) => { calls++; return true; });
        Assert.Equal(CpuLivePowerPlanStatus.InvalidRange, result.Status); Assert.Null(result.Plan); Assert.Equal(0, calls);
    }

    [Fact]
    public void LaterSlowRestorationOutsideRangeRejectsBeforeAnyForwardAdmission()
    {
        int calls = 0;
        var result = Rz090528CpuLivePowerPlan.CreateSelective(Raw(Reply(85000, 100123)),
            [new(CpuLivePowerLimit.Fast, 84), new(CpuLivePowerLimit.Slow, 80)], Cap(71), CpuLivePowerCapability.TryDecode(2),
            new(0x50001, 0, 100000, 1), (_, _) => { calls++; return true; });
        Assert.Equal(CpuLivePowerPlanStatus.InvalidRange, result.Status); Assert.Null(result.Plan); Assert.Equal(0, calls);
    }

    [Fact]
    public void ChangedPlanRequiresExplicitCapabilityCapAndSupportedRangeBeforeAdmission()
    {
        var capability = CpuLivePowerCapability.TryDecode(2)!;
        Assert.Null(CpuLivePowerCapability.TryDecode(uint.MaxValue));
        var cases = new (CpuLivePowerFusedCap?, CpuLivePowerCapability?, CpuLivePowerTrustedRange?, CpuLivePowerPlanStatus)[]
        {
            (null, capability, Range(), CpuLivePowerPlanStatus.InvalidCap),
            (Cap(71), null, Range(), CpuLivePowerPlanStatus.InvalidCapability),
            (Cap(71), CpuLivePowerCapability.TryDecode(uint.MaxValue), Range(), CpuLivePowerPlanStatus.InvalidCapability),
            (Cap(71), capability, null, CpuLivePowerPlanStatus.InvalidRange),
            (Cap(71), capability, new(0x50000, 0, 125000, 1), CpuLivePowerPlanStatus.InvalidRange),
            (Cap(71), capability, new(0x50001, 0, 125000, 0), CpuLivePowerPlanStatus.InvalidRange),
            (Cap(71), capability, new(0x50001, 0, 125000, 2), CpuLivePowerPlanStatus.InvalidRange),
            (Cap(71), capability, new(0x50001, 125000, 0, 1), CpuLivePowerPlanStatus.InvalidRange),
            (Cap(71), capability, new(0x50001, 0, 1023001, 1), CpuLivePowerPlanStatus.InvalidRange),
        };
        foreach (var (cap, candidate, range, expected) in cases)
        {
            int calls = 0;
            var result = Rz090528CpuLivePowerPlan.CreateSelective(Raw(Reply()), [new(CpuLivePowerLimit.Fast, 84)],
                cap, candidate, range, (_, _) => { calls++; return true; });
            Assert.Equal(expected, result.Status); Assert.Null(result.Plan); Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void NoOpNeedsNeitherCapabilityNorRangeAndGrantsNoPlan()
    {
        foreach (CpuLivePowerTarget[] targets in new CpuLivePowerTarget[][] { [], [new(CpuLivePowerLimit.Fast, 85)] })
        {
            var result = Rz090528CpuLivePowerPlan.CreateSelective(Raw(Reply()), targets, null, null, null, null);
            Assert.Equal(CpuLivePowerPlanStatus.Unchanged, result.Status); Assert.Null(result.Plan); Assert.NotNull(result.Baseline);
        }
    }

    // Explicit data fixtures preserve the original ceiling-enforced test cases;
    // neither these candidates nor the pure planner can authorize native writes.
    private static CpuLivePowerPlanResult Plan(CpuPmRawSnapshot? observation, IReadOnlyList<CpuLivePowerTarget>? targets,
        CpuLivePowerFusedCap? cap, Func<CpuLivePowerCommand, CpuLivePowerCommandPurpose, bool>? admit) =>
        Rz090528CpuLivePowerPlan.CreateSelective(observation, targets, cap, CpuLivePowerCapability.TryDecode(0), Range(), admit);
    private static CpuLivePowerTrustedRange Range() => new(0x50001, 0, 125000, 1);
    private static CpuLivePowerFusedCap Cap(uint watts) => CpuLivePowerFusedCap.TryDecode(watts >> 4, (watts & 15) << 28)!;
    private static uint Bits(uint milliwatts) => BitConverter.SingleToUInt32Bits((float)milliwatts * 0.001f);
    private static void Put(byte[] bytes, int offset, uint word) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), word);
    private static byte[] Reply(uint fast = 85000, uint slow = 80000)
    {
        var bytes = new byte[0xD54]; Put(bytes, 0, 0xD50);
        Put(bytes, 4, Bits(60000)); Put(bytes, 8, BitConverter.SingleToUInt32Bits(10f));
        Put(bytes, 12, Bits(fast)); Put(bytes, 16, BitConverter.SingleToUInt32Bits(11f));
        Put(bytes, 20, Bits(slow)); Put(bytes, 24, BitConverter.SingleToUInt32Bits(12f)); return bytes;
    }
    private static CpuPmRawSnapshot Raw(byte[] reply)
    {
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 123, out var raw));
        return raw!;
    }
}

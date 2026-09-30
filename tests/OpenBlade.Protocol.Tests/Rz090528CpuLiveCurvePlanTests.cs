using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLiveCurvePlanTests
{
    private static readonly int[] PhysicalIds = [0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15];

    [Theory]
    [InlineData(0, -1, 0x0000FFFFu)]
    [InlineData(0, 0, 0x00000000u)]
    [InlineData(8, -1, 0x1000FFFFu)]
    [InlineData(15, -1, 0x1070FFFFu)]
    [InlineData(0, short.MinValue, 0x00008000u)]
    [InlineData(15, short.MaxValue, 0x10707FFFu)]
    public void CodecPreservesSignedValuesAndPhysicalSelectors(int physicalId, int value, uint argument)
    {
        Assert.True(CpuLiveCurveCommand.TryCreate(physicalId, value, 0xFF55, out var command));
        Assert.Equal(0x53u, CpuLiveCurveCommand.Message);
        Assert.Equal(argument, command.EncodeArgument());
        Assert.Equal(0u, command.EncodeArgument() & 0xEF8F0000);
        Assert.True(CpuLiveCurveCommand.TryDecode(0x53, argument, 0xFF55, out var decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void CodecArgumentFixturesAreLittleEndianAndDoNotSignExtend()
    {
        Assert.True(CpuLiveCurveCommand.TryCreate(8, -1, 0xFF55, out var command));
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, command.EncodeArgument());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0, 0x10 }, bytes);
        Assert.False(CpuLiveCurveCommand.TryDecode(0x53, 0xFFFFFFFF, 0xFF55, out _));
    }

    [Theory]
    [InlineData(-1, -1, 0xFF55u)]
    [InlineData(1, -1, 0xFF55u)]
    [InlineData(3, -1, 0xFF55u)]
    [InlineData(5, -1, 0xFF55u)]
    [InlineData(7, -1, 0xFF55u)]
    [InlineData(16, -1, 0xFF55u)]
    [InlineData(63, -1, 0xFF55u)]
    [InlineData(0, -32769, 0xFF55u)]
    [InlineData(0, 32768, 0xFF55u)]
    [InlineData(0, -1, 0xFFFFu)]
    [InlineData(0, -1, 0u)]
    public void CodecRejectsUnadmittedIdsMasksAndUnrepresentableValues(int id, int value, uint mask)
    {
        Assert.False(CpuLiveCurveCommand.TryCreate(id, value, mask, out _));
    }

    [Fact]
    public void DecoderRejectsEveryReservedArgumentBitAndEveryOtherCommand()
    {
        foreach (int bit in Enumerable.Range(0, 32).Where(bit => (0xEF8F0000u & (1u << bit)) != 0))
        { Assert.False(CpuLiveCurveCommand.TryDecode(0x53, 1u << bit, 0xFF55, out _)); }
        foreach (uint message in new uint[] { 0, 6, 0x32, 0x33, 0x5D, 0x5E, uint.MaxValue })
        { Assert.False(CpuLiveCurveCommand.TryDecode(message, 0, 0xFF55, out _)); }
        Assert.False(CpuLiveCurveCommand.TryDecode(0x53, 0x00100000, 0xFF55, out _)); // disabled physical ID 1
        Assert.False(CpuLiveCurveCommand.TryDecode(0x53, 0, 0xFFFF, out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("mask")]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("disabled")]
    [InlineData("out-of-range")]
    [InlineData("null-core")]
    [InlineData("null-cores")]
    public void InvalidSnapshotCannotReachAdmission(string fault)
    {
        var cores = Cores().ToList();
        switch (fault)
        {
            case "missing": cores.RemoveAt(0); break;
            case "extra": cores.Add(new(1, -1)); break;
            case "duplicate": cores[0] = cores[1]; break;
            case "disabled": cores[0] = new(1, -1); break;
            case "out-of-range": cores[0] = new(16, -1); break;
            case "null-core": cores[0] = null!; break;
        }
        Rz090528CpuLiveObservation? observation = fault == "null" ? null : new(
            fault == "mask" ? 0xF6BDu : 0xFF55u, fault == "version" ? 0x005D000Au : 0x005D000Bu,
            fault == "length" ? 0xD58 : 0xD50, fault == "null-cores" ? null! : cores);
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(observation, new(true, -2),
            (_, _) => throw new InvalidOperationException("Admission must not run"));
        Assert.Equal(CpuLiveCurvePlanStatus.InvalidBaseline, result.Status);
        Assert.Null(result.Plan);
        Assert.Null(result.Baseline);
    }

    [Fact]
    public void MixedSignedBaselineRestoresEveryOriginalValueIndependently()
    {
        short[] values = [7, -45, short.MinValue, short.MaxValue, 0, -1, -3, -4, -5, -6, -7, -8];
        var cores = PhysicalIds.Select((id, i) => new CpuLiveCoreOffset(id, values[i])).Reverse().ToArray();
        var admissions = new List<(CpuLiveCurveCommand Command, CpuLiveCurveCommandPurpose Purpose)>();
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(cores), new(true, -2), (command, purpose) =>
        { admissions.Add((command, purpose)); return true; });
        Assert.Equal(CpuLiveCurvePlanStatus.Prepared, result.Status);
        var plan = Assert.IsType<Rz090528CpuLiveCurvePlan>(result.Plan);
        Assert.Equal(PhysicalIds, plan.Baseline.CoreOffsets.Select(core => core.PhysicalCoreId));
        Assert.Equal(values, plan.Baseline.CoreOffsets.Select(core => core.Offset));
        Assert.Equal(12, plan.Steps.Count);
        Assert.Equal(24, admissions.Count);
        Assert.All(plan.Expected.CoreOffsets, core => Assert.Equal(-2, core.Offset));
        Assert.Equal(PhysicalIds, plan.Steps.Select(step => step.Command.PhysicalCoreId));
        var restored = plan.Expected;
        foreach (var step in plan.Steps.Reverse())
        {
            Assert.Equal(step.Command.PhysicalCoreId, step.RestoreCommand.PhysicalCoreId);
            Assert.Equal(values[Array.IndexOf(PhysicalIds, step.Command.PhysicalCoreId)], step.RestoreCommand.Offset);
            Assert.Equal(step.After.CoreOffsets, restored.CoreOffsets);
            restored = restored.WithOffset(step.RestoreCommand);
            Assert.Equal(step.Before.CoreOffsets, restored.CoreOffsets);
            Assert.Contains((step.Command, CpuLiveCurveCommandPurpose.Forward), admissions);
            Assert.Contains((step.RestoreCommand, CpuLiveCurveCommandPurpose.Restore), admissions);
        }
        Assert.Equal(plan.Baseline.CoreOffsets, restored.CoreOffsets);
    }

    [Fact]
    public void SelectiveTargetsPreserveOtherCoresAndUsePhysicalIdsAcrossGaps()
    {
        var result = Rz090528CpuLiveCurvePlan.CreateSelective(Observation(), [new(15, 0), new(2, -10)], AdmitAll);
        var plan = Assert.IsType<Rz090528CpuLiveCurvePlan>(result.Plan);
        Assert.Equal(new[] { 2, 15 }, plan.Steps.Select(step => step.Command.PhysicalCoreId));
        Assert.Equal(-10, plan.Expected.CoreOffsets.Single(core => core.PhysicalCoreId == 2).Offset);
        Assert.Equal(0, plan.Expected.CoreOffsets.Single(core => core.PhysicalCoreId == 15).Offset);
        Assert.All(plan.Expected.CoreOffsets.Where(core => core.PhysicalCoreId is not (2 or 15)),
            core => Assert.Equal(-1, core.Offset));
        foreach (var step in plan.Steps)
        {
            Assert.Single(step.Before.CoreOffsets.Zip(step.After.CoreOffsets), pair => pair.First != pair.Second);
        }
    }

    [Fact]
    public void DisabledIntentUsesZeroWithoutChangingRememberedEditorValue()
    {
        var settings = new CpuCurveOptimizerSettings(false, -20);
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(), settings, AdmitAll);
        var plan = Assert.IsType<Rz090528CpuLiveCurvePlan>(result.Plan);
        Assert.All(plan.Steps, step => Assert.Equal(0, step.Command.Offset));
        Assert.All(plan.Steps, step => Assert.Equal(-1, step.RestoreCommand.Offset));
        Assert.Equal(-20, settings.Value);
    }

    [Theory]
    [InlineData(true, -31)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(false, -31)]
    [InlineData(true, 1)]
    public void AllCoreTargetsMustRespectEditorContract(bool enabled, int value)
    {
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(), new(enabled, value), AdmitAll);
        Assert.Equal(CpuLiveCurvePlanStatus.InvalidTarget, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void SelectiveMalformedTargetsAreRejectedAsAWhole()
    {
        CpuLiveCurveTarget[][] cases =
        [
            [new(1, -1)], [new(0, -31)], [new(0, 1)], [new(16, 0)], [new(0, 0), new(0, -1)],
            [new(0, 0), null!], Enumerable.Repeat(new CpuLiveCurveTarget(0, -1), 13).ToArray(),
        ];
        foreach (var targets in cases)
        {
            var result = Rz090528CpuLiveCurvePlan.CreateSelective(Observation(), targets,
                (_, _) => throw new InvalidOperationException("Admission must not run"));
            Assert.Equal(CpuLiveCurvePlanStatus.InvalidTarget, result.Status);
            Assert.Null(result.Plan);
        }
    }

    [Fact]
    public void UnmanagedAndNoOpTargetsNeverProduceAPlanOrCallAdmission()
    {
        var baseline = Observation();
        Func<CpuLiveCurveCommand, CpuLiveCurveCommandPurpose, bool> forbidden = (_, _) => throw new InvalidOperationException();
        var results = new[]
        {
            Rz090528CpuLiveCurvePlan.CreateAllCore(baseline, null, forbidden),
            Rz090528CpuLiveCurvePlan.CreateAllCore(baseline, new(true, -1), forbidden),
            Rz090528CpuLiveCurvePlan.CreateSelective(baseline, null, forbidden),
            Rz090528CpuLiveCurvePlan.CreateSelective(baseline, [], forbidden),
            Rz090528CpuLiveCurvePlan.CreateSelective(baseline, [new(8, -1)], forbidden),
        };
        Assert.All(results, result =>
        {
            Assert.Equal(CpuLiveCurvePlanStatus.Unchanged, result.Status);
            Assert.NotNull(result.Baseline);
            Assert.Null(result.Plan);
        });
    }

    [Fact]
    public void EveryNeededReverseRequiresIndependentPermissionIncludingPositiveOriginal()
    {
        var cores = Cores();
        cores[0] = new(0, 12);
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(cores), new(true, -2),
            (command, purpose) => purpose == CpuLiveCurveCommandPurpose.Forward || command.Offset is >= -30 and <= 0);
        Assert.Equal(CpuLiveCurvePlanStatus.AdmissionDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(12, result.Baseline!.CoreOffsets[0].Offset);
        Assert.Equal(CpuLiveCurvePlanStatus.AdmissionDenied,
            Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(), new(true, -2), null).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateAdmissionDenialOrExceptionNeverExposesPartiallyAdmittedPlan(bool throws)
    {
        int calls = 0;
        var result = Rz090528CpuLiveCurvePlan.CreateAllCore(Observation(), new(true, -2), (_, _) =>
        {
            if (++calls < 24) { return true; }
            if (throws) { throw new IOException("private admission detail"); }
            return false;
        });
        Assert.Equal(24, calls);
        Assert.Equal(CpuLiveCurvePlanStatus.AdmissionDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.All(result.Baseline!.CoreOffsets, core => Assert.Equal(-1, core.Offset));
    }

    [Fact]
    public void CallerMutationCannotChangeBaselineStepsOrTargetsDuringAdmission()
    {
        var cores = Cores();
        CpuLiveCurveTarget[] targets = [new(8, -2), new(0, 0)];
        var result = Rz090528CpuLiveCurvePlan.CreateSelective(Observation(cores), targets, (_, _) =>
        {
            cores[0] = new(0, 123);
            targets[0] = new(15, -30);
            return true;
        });
        var plan = Assert.IsType<Rz090528CpuLiveCurvePlan>(result.Plan);
        Assert.All(plan.Baseline.CoreOffsets, core => Assert.Equal(-1, core.Offset));
        Assert.Equal(new[] { 0, 8 }, plan.Steps.Select(step => step.Command.PhysicalCoreId));
        Assert.Equal(-2, plan.Expected.CoreOffsets.Single(core => core.PhysicalCoreId == 8).Offset);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLiveCoreOffset>)plan.Baseline.CoreOffsets)[0] = new(0, 99));
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLiveCurveStep>)plan.Steps)[0] = plan.Steps[1]);
    }

    private static bool AdmitAll(CpuLiveCurveCommand command, CpuLiveCurveCommandPurpose purpose) => true;
    private static CpuLiveCoreOffset[] Cores() => PhysicalIds.Select(id => new CpuLiveCoreOffset(id, -1)).ToArray();
    private static Rz090528CpuLiveObservation Observation(IReadOnlyList<CpuLiveCoreOffset>? cores = null) =>
        new(0xFF55, 0x005D000B, 0xD50, cores ?? Cores());
}

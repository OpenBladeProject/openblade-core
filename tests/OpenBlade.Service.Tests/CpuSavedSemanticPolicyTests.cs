using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuSavedSemanticPolicyTests
{
    [Fact]
    public void ActualEncoderMatchesIndependentKnownAnswers() => Assert.True(CpuSavedSemanticPolicy.MatchesKnownAnswers());

    [Theory]
    [InlineData("010005000100FF11")]
    [InlineData("0A0005000100EF11")]
    [InlineData("0A0005000100FF1100")]
    [InlineData("0A0005000100FF")]
    public void WrongSelectorArgumentOrGeometryDoesNotMatchEncoding(string hex) =>
        Assert.False(CpuSavedSemanticPolicy.MatchesEncoding(new(CpuSavedTuningCommandKind.CurveOptimizer, -1), Convert.FromHexString(hex)));

    [Fact]
    public void PowerRequiresIntegerWattToMilliwattEncoding()
    {
        var command = new CpuSavedTuningCommand(CpuSavedTuningCommandKind.PowerLimit, 45);
        Assert.True(CpuSavedSemanticPolicy.MatchesEncoding(command, Convert.FromHexString("01000500C8AF0000")));
        Assert.False(CpuSavedSemanticPolicy.MatchesEncoding(command, Convert.FromHexString("010005002D000000")));
        Assert.False(CpuSavedSemanticPolicy.MatchesEncoding(command, Convert.FromHexString("0100050000003442")));
    }

    [Fact]
    public void InvalidCommandFamiliesAndValuesAreRejected()
    {
        CpuSavedTuningCommand[] invalid =
        [
            new((CpuSavedTuningCommandKind)99, 0),
            new(CpuSavedTuningCommandKind.CurveOptimizer, -31),
            new(CpuSavedTuningCommandKind.CurveOptimizer, 1),
            new(CpuSavedTuningCommandKind.PowerLimit, 0),
            new(CpuSavedTuningCommandKind.PowerLimit, 44),
            new(CpuSavedTuningCommandKind.PowerLimit, 96),
            new(CpuSavedTuningCommandKind.ResetPowerLimit, 45),
        ];
        foreach (CpuSavedTuningCommand command in invalid)
        {
            Assert.False(CpuSavedSemanticPolicy.MatchesEncoding(command, new byte[8]));
        }
        Assert.False(CpuSavedSemanticPolicy.MatchesEncoding(null, new byte[8]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCombinedPlanMatchesFullPairAndOriginalRestoration(bool customPower)
    {
        byte[] baseline = Baseline(customPower);
        byte[] original = (byte[])baseline.Clone();
        var settings = new CpuTuningSettings(45, new(true, -30));
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        Assert.Equal(2, steps.Length);
        Assert.True(CpuSavedSemanticPolicy.MatchesPlan(baseline, original, settings, steps));
        Assert.Equal(customPower ? new(CpuSavedTuningCommandKind.PowerLimit, 65)
            : new CpuSavedTuningCommand(CpuSavedTuningCommandKind.ResetPowerLimit, 0), steps[0].RestoreCommand);
        Assert.Equal(new(CpuSavedTuningCommandKind.CurveOptimizer, -1), steps[1].RestoreCommand);
        byte[] restored = steps[^1].After;
        foreach (CpuSavedTuningStep step in steps.Reverse()) { restored = CpuSavedTuningPlan.Simulate(restored, step.RestoreCommand); }
        Assert.Equal(original, restored);
        Assert.Equal(original, baseline);
    }

    [Theory]
    [InlineData(0x94)]
    [InlineData(0x95)]
    [InlineData(0x96)]
    [InlineData(0x9A)]
    [InlineData(0xA9)]
    [InlineData(0xC2)]
    [InlineData(0x104)]
    [InlineData(799)]
    public void IncorrectFieldSiblingOrUnrelatedByteRejectsWholePlan(int offset)
    {
        byte[] baseline = Baseline(true);
        var settings = new CpuTuningSettings(95, null);
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        steps[0].After[offset] ^= 1;
        Assert.False(CpuSavedSemanticPolicy.MatchesStep(steps[0]));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
    }

    [Fact]
    public void CommonModeChecksAllFivePowerWordsEvenForCurveOnly()
    {
        byte[] baseline = Baseline(false);
        var settings = new CpuTuningSettings(null, new(false, -30));
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        Assert.True(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
        Assert.Equal(0xFF, steps[0].After[0x95]);
        Assert.Equal(new(CpuSavedTuningCommandKind.CurveOptimizer, 0), steps[0].Command);
        steps[0].After[0x95] = 2;
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));

        // A nonzero fourth sibling prevents common completion selecting FF;
        // CO must retain the existing power mode, not rewrite it to custom.
        baseline[0xA6] = 1;
        baseline[0x95] = 3;
        steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        Assert.True(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
        Assert.Equal(3, steps[0].After[0x95]);
        steps[0].After[0x95] = 0xFF;
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
    }

    [Fact]
    public void ResetCannotReplaceCustomOriginalOrBecomeAnEditableForward()
    {
        byte[] baseline = Baseline(true);
        var settings = new CpuTuningSettings(45, null);
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        steps[0] = steps[0] with { RestoreCommand = new(CpuSavedTuningCommandKind.ResetPowerLimit, 0) };
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
        steps[0] = steps[0] with { Command = new(CpuSavedTuningCommandKind.ResetPowerLimit, 0) };
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, new(0, null), steps));
    }

    [Fact]
    public void WrongReverseAndStepOrderOrOmissionAreRejected()
    {
        byte[] baseline = Baseline(true);
        var settings = new CpuTuningSettings(95, new(true, -30));
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps.Reverse().ToArray()));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps[..1]));
        steps[1] = steps[1] with { RestoreCommand = new(CpuSavedTuningCommandKind.CurveOptimizer, 0) };
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
    }

    [Theory]
    [InlineData("positive-curve")]
    [InlineData("per-core")]
    [InlineData("curve-outside-range")]
    [InlineData("fractional-power")]
    [InlineData("power-outside-range")]
    [InlineData("default-with-sibling")]
    [InlineData("wrong-policy")]
    public void ChangedUnsupportedOriginalHasNoMatchingRestorablePlan(string fault)
    {
        byte[] baseline = Baseline(false);
        CpuTuningSettings settings = new(45, new(true, -30));
        switch (fault)
        {
            case "positive-curve": baseline[0xC1] = 0; break;
            case "per-core": baseline[0xC0] = 2; break;
            case "curve-outside-range": baseline[0xC2] = 31; break;
            case "fractional-power": SetCustomPower(baseline, 65001); break;
            case "power-outside-range": SetCustomPower(baseline, 44000); break;
            case "default-with-sibling": baseline[0x9A] = 1; break;
            case "wrong-policy": baseline[0x94] = 1; break;
            default: throw new InvalidOperationException();
        }
        Assert.Null(CpuSavedTuningPlan.Create(baseline, settings));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, []));
    }

    [Fact]
    public void MalformedOrMismatchingPairsAndSettingsAreRejected()
    {
        byte[] baseline = Baseline(false);
        var settings = new CpuTuningSettings(45, null);
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        byte[] last = (byte[])baseline.Clone();
        last[799] ^= 1;
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, last, settings, steps));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline[..799], baseline[..799], settings, steps));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(new byte[801], new byte[801], settings, steps));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, null, steps));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, new(null, null), []));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, new(null, new(false, 0)), []));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, null));
        Assert.False(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, [steps[0] with { Before = new byte[799] }]));
    }

    [Fact]
    public void AlreadyMatchingSettingsRequireAnEmptyPlan()
    {
        byte[] baseline = Baseline(true);
        var settings = new CpuTuningSettings(65, new(true, -1));
        CpuSavedTuningStep[] steps = CpuSavedTuningPlan.Create(baseline, settings)!;
        Assert.Empty(steps);
        Assert.True(CpuSavedSemanticPolicy.MatchesPlan(baseline, baseline, settings, steps));
    }

    private static byte[] Baseline(bool customPower)
    {
        // Deliberately synthetic sentinels exercise preservation outside the
        // command fields, including inactive per-core and coupled state.
        byte[] bytes = Enumerable.Range(0, 800).Select(index => (byte)(index % 251)).ToArray();
        bytes[0x94] = 2;
        bytes[0x95] = 0xFF;
        bytes.AsSpan(0x96, 20).Clear();
        bytes[0xC0] = 1;
        bytes[0xC1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xC2), 1);
        if (customPower)
        {
            SetCustomPower(bytes, 65000);
            for (int index = 0; index < 4; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x9A + index * 4), (uint)(index + 7));
            }
        }
        return bytes;
    }

    private static void SetCustomPower(byte[] bytes, uint milliwatts)
    {
        bytes[0x95] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x96), milliwatts);
    }
}

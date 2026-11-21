using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadAutoCurvesTests
{
    // Synapse product 3907 default profile (fanController.smart), as temperature:rpm.
    [Theory]
    [InlineData(CoolingPadAutoProfile.Quiet, "40:500,50:500,85:1000,90:1600,100:1600", "30:500,40:500,75:1000,80:1600,90:1600")]
    [InlineData(CoolingPadAutoProfile.Balanced, "40:1000,90:2200,100:2200", "30:1000,80:2200,90:2200")]
    [InlineData(CoolingPadAutoProfile.Performance, "40:1000,55:2000,80:2700,100:2700", "30:1000,45:2000,70:2700,90:2700")]
    public void TablesMatchTheSynapseDefaults(CoolingPadAutoProfile profile, string cpu, string gpu)
    {
        Assert.Equal(cpu, Format(CoolingPadAutoCurves.Cpu(profile)));
        Assert.Equal(gpu, Format(CoolingPadAutoCurves.Gpu(profile)));
        // Every table is a valid pad curve.
        Assert.True(CoolingPadSettings.IsWellFormed(new CoolingPadSettings
        {
            CpuCurve = CoolingPadAutoCurves.Cpu(profile),
            GpuCurve = CoolingPadAutoCurves.Gpu(profile),
        }));
    }

    [Theory]
    [InlineData(CoolingPadAutoProfile.Quiet, CoolingPadFanPreset.Quiet)]
    [InlineData(CoolingPadAutoProfile.Balanced, CoolingPadFanPreset.Balanced)]
    [InlineData(CoolingPadAutoProfile.Performance, CoolingPadFanPreset.Performance)]
    public void ProfilesSelectTheMatchingFirmwareSmartPreset(CoolingPadAutoProfile profile, CoolingPadFanPreset preset)
    {
        Assert.Equal(preset, CoolingPadAutoCurves.Preset(profile));
        Assert.Equal((byte)(3 + (int)profile), (byte)preset);
    }

    [Theory]
    // The speed of the smallest point strictly above the temperature; no interpolation.
    [InlineData(0, 500)]
    [InlineData(39, 500)]
    [InlineData(40, 500)]
    [InlineData(49, 500)]
    [InlineData(50, 1000)]
    [InlineData(84, 1000)]
    [InlineData(85, 1600)]
    [InlineData(99, 1600)]
    // Synapse finds no point here and sends 0; OpenBlade keeps the last point's speed.
    [InlineData(100, 1600)]
    [InlineData(115, 1600)]
    public void EvaluationIsSynapsesStepFunction(int temperature, int expected) =>
        Assert.Equal(expected, CoolingPadAutoCurves.Evaluate(CoolingPadAutoCurves.Cpu(CoolingPadAutoProfile.Quiet), temperature));

    [Fact]
    public void SourcesSelectTheirTableAndBothTakesTheHigherResult()
    {
        Assert.Equal(2000, CoolingPadAutoCurves.Target(CoolingPadAutoProfile.Performance, CoolingPadTemperatureSource.Cpu, 50, null));
        Assert.Equal(2700, CoolingPadAutoCurves.Target(CoolingPadAutoProfile.Performance, CoolingPadTemperatureSource.Gpu, null, 50));
        Assert.Equal(2700, CoolingPadAutoCurves.Target(CoolingPadAutoProfile.Performance, CoolingPadTemperatureSource.Both, 50, 50));
        Assert.Equal(2200, CoolingPadAutoCurves.Target(CoolingPadAutoProfile.Balanced, CoolingPadTemperatureSource.Both, 95, 20));
        Assert.Throws<InvalidOperationException>(() =>
            CoolingPadAutoCurves.Target(CoolingPadAutoProfile.Balanced, CoolingPadTemperatureSource.Both, 60, null));
    }

    private static string Format(IReadOnlyList<CoolingPadCurvePoint> points) =>
        string.Join(",", points.Select(point => $"{point.TemperatureCelsius}:{point.Rpm}"));
}

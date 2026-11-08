using OpenBlade.Contracts;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopInitialAdmissionDiagnosticsTests
{
    [Theory]
    [InlineData("ActiveConsoleSession", "consoleSession=0; boundSession=0")]
    [InlineData("ServiceRunning", "serviceStatus=Stopped")]
    [InlineData("ServiceWriterOwnership", "writerCount=2; originalWriterPresent=True")]
    [InlineData("CpuReaderPreparation", "outcome=NotReady")]
    [InlineData("CpuReaderPreparation", "outcome=TimeoutException; canceled=False")]
    [InlineData("ThermalSample", "outcome=TimeoutException; sampleUnavailable=True")]
    public void InitialRefusalRetainsSpecificPreNativeStageAndObservedDetail(string stage, string detail)
    {
        string message = So6120ServiceInitialAdmissionDiagnostics.Refusal(stage, detail);
        Assert.Contains($"stage={stage};", message);
        Assert.Contains(detail, message);
        Assert.StartsWith("Initial command-free Stop admission refused", message);
    }

    [Theory]
    [InlineData(74, null, true, false, 2, false)]
    [InlineData(null, null, true, false, 1000, false)]
    [InlineData(52, null, false, false, 1500, false)]
    [InlineData(52, 50, true, false, 2001, true)]
    public void ThermalRefusalReportsActualSampleWithoutInferringGpuState(int? cpu, int? gpu,
        bool inactive, bool poweredDown, int milliseconds, bool canceled)
    {
        string message = So6120ServiceInitialAdmissionDiagnostics.ThermalRefusal(FanStopSettings.Default,
            new(cpu, gpu, inactive, poweredDown), TimeSpan.FromMilliseconds(milliseconds), canceled);
        Assert.Contains("stage=ThermalEntry; mode=Auto;", message);
        Assert.Contains($"cpuC={cpu?.ToString() ?? "unavailable"};", message);
        Assert.Contains($"gpuC={gpu?.ToString() ?? "unavailable"};", message);
        Assert.Contains($"gpuInactive={inactive}; gpuPoweredDown={poweredDown};", message);
        Assert.Contains($"sampleAgeMs={milliseconds}.0; sampleCanceled={canceled};", message);
        Assert.Contains("cpuEntryLimitC=55; gpuEntryLimitC=inactive-required; maxSampleAgeMs=2000", message);
        Assert.True(message.Length < 700);
    }

    [Fact]
    public void CustomRefusalRetainsItsOwnLimitsAndBoundsOnlyDiagnosticAge()
    {
        FanStopSettings settings = FanStopSettings.Default with
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = 60,
            GpuLimitCelsius = 65,
            CpuImmediateCutoffCelsius = 80,
            GpuImmediateCutoffCelsius = 75
        };
        string message = So6120ServiceInitialAdmissionDiagnostics.ThermalRefusal(settings,
            new(61, 66, false, false), TimeSpan.FromDays(1), false);
        Assert.Contains("mode=Custom; cpuC=61; gpuC=66;", message);
        Assert.Contains("sampleAgeMs=60000.0;", message);
        Assert.Contains("cpuEntryLimitC=80; gpuEntryLimitC=75;", message);
    }
}

using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadControlStateQueryTests
{
    [Fact]
    public async Task Output_PreservesRawCandidateAndClosedProductionGates()
    {
        var output = new StringWriter();
        var command = new CoolingPadControlStateQueryCommand(
            _ => Task.FromResult(new HidSessionResult<GetterCandidateProbeSnapshot>(new("0300"), null)),
            output);
        Assert.Equal(0, await command.RunAsync([], default));
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("0F43", root.GetProperty("device").GetProperty("productId").GetString());
        Assert.Equal("0300", root.GetProperty("responsePayloadHex").GetString());
        Assert.False(root.GetProperty("semanticMappingConfirmed").GetBoolean());
        Assert.False(root.GetProperty("typedReadbackAdmitted").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.True(root.GetProperty("deviceBiosIsCaptureProvenance").GetBoolean());
        Assert.DoesNotContain("Driver", output.ToString());
    }

    [Fact]
    public async Task Arguments_DoNotExposeAlternateCommandsOrReportIds()
    {
        var called = false;
        var command = new CoolingPadControlStateQueryCommand(_ =>
        {
            called = true;
            throw new InvalidOperationException();
        }, new StringWriter());
        Assert.Equal(1, await command.RunAsync(["05"], default));
        Assert.False(called);
    }

    [Fact]
    public void PadGuard_RetainsAppEngineAndVendorServicesAsConflicts()
    {
        var guard = new CoolingPadControlStateQueryFactory.PadAccessGuard(new Processes(), new Services());
        var decision = guard.Check();
        Assert.Contains("RazerAppEngine", decision.Conflicts);
        Assert.Contains("USBPcapCMD", decision.Conflicts);
        Assert.Contains("Razer Game Manager Service 3", decision.Conflicts);
        Assert.False(decision.Safe);
    }

    [Fact]
    public void PadGuard_FailsClosedWhenCaptureProcessDetectionFails()
    {
        var guard = new CoolingPadControlStateQueryFactory.PadAccessGuard(new FailingProcesses(), new Services());
        var decision = guard.Check();
        Assert.False(decision.Safe);
        Assert.NotNull(decision.DetectionFailure);
    }

    private sealed class FailingProcesses : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => throw new InvalidOperationException();
    }

    private sealed class Processes : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => ["RazerAppEngine", "USBPcapCMD"];
    }

    private sealed class Services : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() =>
            [new("Razer Game Manager Service 3", "Razer Game Manager Service 3", WindowsServiceState.Running)];
    }
}

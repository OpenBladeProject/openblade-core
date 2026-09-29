using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528PadlessHyperBoostValidationCommandTests
{
    [Fact]
    public async Task ConfirmationAndPlanHashAreRequiredBeforeCreatingRunner()
    {
        using var output = new StringWriter();
        int result = await Rz090528PadlessHyperBoostValidationCommand.RunAsync([], _ => throw new Exception(),
            new Gpu(), new Gpu(), () => throw new Exception(), _ => throw new Exception(), output, CancellationToken.None);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task NonzeroIndependentGpuBaselineBlocksActorOpenWithoutChangingOffsets()
    {
        var primary = new Gpu();
        var independent = new Gpu { Current = 5 };
        string[] args = ["--confirm-target", Rz090528PadlessHyperBoostValidationCommand.Confirmation,
            "--confirm-pad-disconnected", Rz090528PadlessHyperBoostValidationCommand.AttendedConfirmation,
            "--plan-sha256", Rz090528PadlessHyperBoostValidation.PlanSha256];
        using var output = new StringWriter();
        int result = await Rz090528PadlessHyperBoostValidationCommand.RunAsync(args,
            guard => new(new NeverOpen(), new AbsentPad(), guard), primary, independent, () => true,
            _ => ValueTask.FromResult(new FanTemperatureReadings(45, null, true)), output, CancellationToken.None);
        Assert.Equal(3, result);
        Assert.Contains("\"FinalGpuZeroConfirmed\": false", output.ToString());
        Assert.Equal(5, independent.Current);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, 40, false)]
    [InlineData(45, -1, false)]
    [InlineData(45, null, false)]
    public async Task MissingOrInvalidRequiredTelemetryNeverCreatesActor(int? cpu, int? gpu, bool inactive)
    {
        string[] args = ["--confirm-target", Rz090528PadlessHyperBoostValidationCommand.Confirmation,
            "--confirm-pad-disconnected", Rz090528PadlessHyperBoostValidationCommand.AttendedConfirmation,
            "--plan-sha256", Rz090528PadlessHyperBoostValidation.PlanSha256];
        using var output = new StringWriter();
        int result = await Rz090528PadlessHyperBoostValidationCommand.RunAsync(args,
            _ => throw new InvalidOperationException("Must reject before actor creation."), new Gpu(), new Gpu(), () => true,
            _ => ValueTask.FromResult(new FanTemperatureReadings(cpu, gpu, inactive)), output, CancellationToken.None);
        Assert.Equal(3, result);
        Assert.Contains("\"HardwareOpened\": false", output.ToString());
    }

    private sealed class Gpu : IGpuClockOffsetValidationController, IGpuClockOffsetProbe
    {
        internal int Current;
        public GpuClockOffsetCapabilities Query() => new("NVIDIA GeForce RTX 5090 Laptop GPU",
            new(-15, 15, Current), new(-15, 15, Current));
        public void Set(GpuClockOffsetValidationSettings settings) => throw new InvalidOperationException("No GPU setter is allowed.");
    }
    private sealed class AbsentPad : ICoolingPadDiscoverySource
    {
        public CoolingPadConnectionState Observe() => CoolingPadConnectionState.Absent;
    }
    private sealed class NeverOpen : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) => throw new InvalidOperationException("The GPU preflight must prevent opening HID.");
    }
}

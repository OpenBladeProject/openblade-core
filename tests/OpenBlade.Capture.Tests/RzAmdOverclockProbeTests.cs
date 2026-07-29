using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class RzAmdOverclockProbeTests
{
    [Fact]
    public async Task RunAsync_PrintsSanitizedReadOnlyState()
    {
        var output = new StringWriter();
        var probe = new StubProbe(new CpuPowerServiceState(60, -1, "1.1.15.0"));

        int result = await CpuPowerServiceProbeCommand.RunAsync(
            probe,
            output,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Backend=RzAMDOverClock/RzDLLService", output.ToString());
        Assert.Contains("BackendVersion=1.1.15.0", output.ToString());
        Assert.Contains("PowerLimitWatts=60", output.ToString());
        Assert.Contains("CurveOptimizerValue=-1", output.ToString());
        Assert.Contains("CurveOptimizerEnabled=True", output.ToString());
        Assert.Contains("SetterExportsInvoked=False", output.ToString());
        Assert.Contains("SynapseProcessGuard=True", output.ToString());
        Assert.Contains("Success=True", output.ToString());
    }

    [Fact]
    public async Task RunAsync_RejectsValuesOutsideExactDevicePolicy()
    {
        var output = new StringWriter();
        var probe = new StubProbe(new CpuPowerServiceState(96, -1, "1.1.15.0"));

        int result = await CpuPowerServiceProbeCommand.RunAsync(
            probe,
            output,
            CancellationToken.None);

        Assert.Equal(2, result);
        Assert.Contains("outside the exact-device policy", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void TryParseValue_AcceptsExactSuccessfulResponse()
    {
        const string message =
            """
            {"dllFunc":"GetPowerLimitValue","dllName":"RzAMDOverClockDLL","error":"","powerLimit":60,"status":"SUCCESS"}
            """;

        bool parsed = WindowsRzAmdOverclockProbe.TryParseValue(
            message,
            "GetPowerLimitValue",
            "powerLimit",
            out int value);

        Assert.True(parsed);
        Assert.Equal(60, value);
    }

    [Theory]
    [InlineData(
        """{"dllFunc":"SetPowerLimitValue","dllName":"RzAMDOverClockDLL","error":"","powerLimit":60,"status":"SUCCESS"}""")]
    [InlineData(
        """{"dllFunc":"GetPowerLimitValue","dllName":"Other","error":"","powerLimit":60,"status":"SUCCESS"}""")]
    [InlineData(
        """{"dllFunc":"GetPowerLimitValue","dllName":"RzAMDOverClockDLL","error":"failed","powerLimit":60,"status":"FAILURE"}""")]
    public void TryParseValue_RejectsUnrelatedOrFailedResponse(string message)
    {
        Assert.False(WindowsRzAmdOverclockProbe.TryParseValue(
            message,
            "GetPowerLimitValue",
            "powerLimit",
            out _));
    }

    private sealed class StubProbe(CpuPowerServiceState state) : ICpuPowerServiceProbe
    {
        public Task<CpuPowerServiceState> QueryAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(state);
    }
}

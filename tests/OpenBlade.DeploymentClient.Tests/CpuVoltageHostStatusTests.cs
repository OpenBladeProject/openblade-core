using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class CpuVoltageHostStatusTests
{
    private static readonly CpuVoltageHostEnrollment Old = new(Guid.NewGuid(), "sid", 1, 42, 123,
        @"C:\old\OpenBlade.Capture.exe", new string('1', 40), new string('2', 64), "version");
    private static readonly CpuVoltageHostEnrollment New = Old with
    {
        Session = Guid.NewGuid(),
        HostPid = 43,
        HostImage = @"C:\new\OpenBlade.Capture.exe",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupWaitAllowsMissingOrProvedClosedOldEnrollmentUntilExactNewHost(bool oldPresent)
    {
        StartupClock clock = new();
        int reads = 0, closedChecks = 0;
        var actual = await WindowsCpuVoltageHostStatusVerifier.WaitForEnrollmentAsync(New.HostImage,
            () => ++reads < 4 ? oldPresent ? Old : null : New,
            _ => { }, value => { Assert.Equal(Old, value); closedChecks++; },
            clock.Delay, clock, CancellationToken.None);
        Assert.Equal(New, actual);
        Assert.Equal(4, reads);
        Assert.Equal(oldPresent ? 3 : 0, closedChecks);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.Elapsed);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("wrong-user")]
    [InlineData("invalid-record")]
    public async Task StartupWaitRejectsUncertaintyWithoutPollingThroughIt(string failure)
    {
        StartupClock clock = new();
        int reads = 0;
        await Assert.ThrowsAsync<IOException>(() => WindowsCpuVoltageHostStatusVerifier.WaitForEnrollmentAsync(New.HostImage,
            () => { reads++; return failure == "invalid-record" ? throw new IOException("Invalid record") : Old; },
            _ => { if (failure == "wrong-user") { throw new IOException("Changed user"); } },
            _ => throw new IOException("Unresolved host"), clock.Delay, clock, CancellationToken.None));
        Assert.Equal(1, reads);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public async Task StartupWaitTimesOutWithoutAnyActionReplay()
    {
        StartupClock clock = new();
        await Assert.ThrowsAsync<TimeoutException>(() => WindowsCpuVoltageHostStatusVerifier.WaitForEnrollmentAsync(New.HostImage,
            () => null, _ => { }, _ => { }, clock.Delay, clock, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(60), clock.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCloseAllowsBlockedOnlyWhenNoJobAndNeverAsStartupSuccess(bool activeJob)
    {
        using JsonDocument reply = JsonDocument.Parse(JsonSerializer.Serialize(new
        { session = Old.Session, state = "Blocked", job = activeJob ? Guid.NewGuid() : (Guid?)null, requestAccepted = false }));
        if (activeJob)
        { Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, Old.Session, false, true)); }
        else { WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, Old.Session, false, true); }
        Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, Old.Session, false));
    }

    private sealed class StartupClock : TimeProvider
    {
        internal TimeSpan Elapsed { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        internal Task Delay(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("--cpu-voltage", true, true)]
    [InlineData("--CPU-VOLTAGE", false, false)]
    [InlineData("--cpu-voltage --observe", false, false)]
    [InlineData("--cpu-voltage --cpu-voltage", false, false)]
    [InlineData("--installer path", false, false)]
    public void ArgumentsPermitOnlyDefaultOrSoleCpuFlag(string input, bool valid, bool cpu)
    {
        Assert.Equal(valid, DeploymentClientArguments.TryParse(
            input.Split(' ', StringSplitOptions.RemoveEmptyEntries), out bool actual));
        Assert.Equal(cpu, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionRequestsContainOnlyFixedStatusOrClose(bool close)
    {
        Guid id = Guid.NewGuid();
        using JsonDocument request = JsonDocument.Parse(WindowsCpuVoltageHostStatusVerifier.CreateRequest(id, close));
        Assert.Equal(["version", "requestId", "action"], request.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, request.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(id, request.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal(close ? "Close" : "Status", request.RootElement.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("Ready", false, false, false, true)]
    [InlineData("Closed", true, false, false, true)]
    [InlineData("Running", false, false, false, false)]
    [InlineData("Blocked", false, false, false, false)]
    [InlineData("Ready", true, false, false, false)]
    [InlineData("Ready", false, true, false, false)]
    [InlineData("Ready", false, false, true, false)]
    public void StatusRequiresMatchingIdleStateWithoutJobAcceptance(string state, bool closing, bool job, bool accepted, bool valid)
    {
        Guid session = Guid.NewGuid();
        using JsonDocument reply = JsonDocument.Parse(JsonSerializer.Serialize(new
        { session, state, job = job ? Guid.NewGuid() : (Guid?)null, requestAccepted = accepted }));
        if (valid) { WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, session, closing); }
        else { Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, session, closing)); }
        Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, Guid.NewGuid(), closing));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"session\":123}")]
    public void MalformedStatusCannotAuthorizeCloseOrSuccess(string json)
    {
        using JsonDocument reply = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostStatusVerifier.RequireReply(reply.RootElement, Guid.NewGuid(), false));
    }
}

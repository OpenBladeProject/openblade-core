using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class DirectFanValidationAccessGuardTests
{
    [Theory]
    [InlineData("OpenBlade.Service")]
    [InlineData("openblade.tray")]
    [InlineData("OpenBlade.SessionAgent")]
    [InlineData("USBPcapCMD")]
    public void Check_BlocksInstalledOrCaptureProcessOwners(string processName)
    {
        var guard = new DirectFanValidationAccessGuard(
            new FixedGuard(new HardwareAccessDecision([], null)),
            new ProcessSource([processName]),
            new ServiceSource([]));

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Contains("owner is active", result.Conflicts.Single());
    }

    [Theory]
    [InlineData(WindowsServiceState.Other)]
    [InlineData(WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.StartPending)]
    [InlineData(WindowsServiceState.ContinuePending)]
    public void Check_BlocksOpenBladeServiceUnlessStopped(WindowsServiceState state)
    {
        var guard = new DirectFanValidationAccessGuard(
            new FixedGuard(new HardwareAccessDecision([], null)),
            new ProcessSource([]),
            new ServiceSource([new("OpenBlade", "OpenBlade", state)]));

        Assert.False(guard.Check().Safe);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Check_FailsClosedWhenOwnerInventoryIsUnavailable(bool processFails)
    {
        IRunningProcessSource processes = processFails
            ? new ThrowingProcessSource()
            : new ProcessSource([]);
        IWindowsServiceSource services = processFails
            ? new ServiceSource([])
            : new ThrowingServiceSource();
        var guard = new DirectFanValidationAccessGuard(
            new FixedGuard(new HardwareAccessDecision([], null)),
            processes,
            services);

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal(
            "The validation owner inventory is unavailable.",
            result.DetectionFailure);
    }

    [Fact]
    public void Check_PreservesControllerGuardBlockWithoutReadingOwnerInventory()
    {
        var controllerDecision = new HardwareAccessDecision(
            ["Razer Synapse"],
            "controller inventory warning");
        var guard = new DirectFanValidationAccessGuard(
            new FixedGuard(controllerDecision),
            new ThrowingProcessSource(),
            new ThrowingServiceSource());

        HardwareAccessDecision result = guard.Check();

        Assert.Same(controllerDecision, result);
    }

    private sealed class FixedGuard(HardwareAccessDecision decision) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => decision;
    }

    private sealed class ProcessSource(IReadOnlyList<string> names) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => names;
    }

    private sealed class ServiceSource(IReadOnlyList<WindowsServiceSnapshot> services)
        : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => services;
    }

    private sealed class ThrowingProcessSource : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() =>
            throw new InvalidOperationException("process inventory unavailable");
    }

    private sealed class ThrowingServiceSource : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() =>
            throw new InvalidOperationException("service inventory unavailable");
    }
}

public sealed class DirectFanValidationSafetyGateTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ValidateAsync_RechecksFullPowerWithoutClaimingPadPresence()
    {
        var session = new ScriptedSession(Convert.FromHexString("1111"));
        var gate = new DirectFanValidationSafetyGate(
            new FixedGuard(new HardwareAccessDecision([], null)),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult first = await gate.ValidateAsync(
            new ValidationRunContext(), session, default);
        PerformanceFanValidationSafetyGateResult second = await gate.ValidateAsync(
            new ValidationRunContext(), session, default);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.All(new[] { first, second }, result =>
        {
            Assert.True(result.FullPowerAdapterConfirmed);
            Assert.True(result.CoolingPadNotRequired);
            Assert.False(result.CoolingPadAbsentConfirmed);
            Assert.False(result.CoolingPadPresentConfirmed);
            Assert.False(result.CoolingPadOpaqueZeroConfirmed);
        });
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command =>
            Assert.Equal((0x07, 0x8C), (command.ClassId, command.CommandId)));
    }

    [Theory]
    [InlineData("0011")]
    [InlineData("0711")]
    public async Task ValidateAsync_RejectsBatteryAndUsbCPower(string responseHex)
    {
        var gate = new DirectFanValidationSafetyGate(
            new FixedGuard(new HardwareAccessDecision([], null)),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new ScriptedSession(Convert.FromHexString(responseHex)),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired,
            result.Failure);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAdapterPowerReadFailure()
    {
        var gate = new DirectFanValidationSafetyGate(
            new FixedGuard(new HardwareAccessDecision([], null)),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new ScriptedSession(HidSessionFailureKind.Timeout),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed,
            result.Failure);
    }

    private sealed class FixedGuard(HardwareAccessDecision decision) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => decision;
    }

    private sealed class ScriptedSession : IRazerCommandSession
    {
        private readonly HidSessionResult<ReadOnlyMemory<byte>> result;

        internal ScriptedSession(byte[] response)
        {
            result = new(response, null);
        }

        internal ScriptedSession(HidSessionFailureKind failure)
        {
            result = new(default, failure);
        }

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        internal List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            _ = assignTransactionId();
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

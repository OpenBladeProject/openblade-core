using OpenSynapse.Capture;
using OpenSynapse.Protocol;

namespace OpenSynapse.Capture.Tests;

public sealed class BatteryQuerySessionTests
{
    [Fact]
    public async Task RunAsync_PerformsExactlyOneQueryNoApplyAndDisposes()
    {
        var port = new CountingPort(new(BatteryValidationSetting.Limit80, null));
        var session = new BatteryQuerySession(new StubFactory(new(port, null)));

        BatteryQueryResult result = await session.RunAsync(default);

        Assert.True(result.Success);
        Assert.Equal(BatteryValidationSetting.Limit80, result.Setting);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(0, port.ApplyCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_PreservesSafeQueryFailureDiagnosticWithoutApply()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 31,
            TransferredByteCount: 12);
        var port = new CountingPort(
            new(default, HidSessionFailureKind.NativeIoFailure)
            {
                Diagnostic = diagnostic,
            });
        var session = new BatteryQuerySession(new StubFactory(new(port, null)));

        BatteryQueryResult result = await session.RunAsync(default);

        Assert.Equal(BatteryQueryFailure.QueryFailed, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(0, port.ApplyCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.IdentityBlocked, BatteryQueryFailure.WrongTarget)]
    [InlineData(HidSessionFailureKind.ConflictBlocked, BatteryQueryFailure.Conflict)]
    [InlineData(HidSessionFailureKind.ConflictDetectionFailed, BatteryQueryFailure.ConflictDetectionFailed)]
    [InlineData(HidSessionFailureKind.NativeIoFailure, BatteryQueryFailure.OpenFailed)]
    public async Task RunAsync_MapsOpenFailureWithoutQuery(
        HidSessionFailureKind failure,
        BatteryQueryFailure expected)
    {
        var session = new BatteryQuerySession(
            new StubFactory(new(null, failure)));

        BatteryQueryResult result = await session.RunAsync(default);

        Assert.Equal(expected, result.Failure);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_RejectsUndefinedSettingAndStillDisposes()
    {
        var port = new CountingPort(new((BatteryValidationSetting)999, null));
        var session = new BatteryQuerySession(new StubFactory(new(port, null)));

        BatteryQueryResult result = await session.RunAsync(default);

        Assert.Equal(BatteryQueryFailure.UndefinedSetting, result.Failure);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(0, port.ApplyCount);
        Assert.Equal(1, port.DisposeCount);
    }

    private sealed class StubFactory(HidSessionResult<IBatteryValidationPort> result)
        : IBatteryValidationRunFactory
    {
        public Task<HidSessionResult<IBatteryValidationPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CountingPort(
        HidSessionResult<BatteryValidationSetting> queryResult) : IBatteryValidationPort
    {
        public int QueryCount { get; private set; }
        public int ApplyCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            CancellationToken cancellationToken)
        {
            QueryCount++;
            return Task.FromResult(queryResult);
        }

        public Task<HidSessionResult<HidUnit>> ApplyAsync(
            BatteryValidationSetting setting,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

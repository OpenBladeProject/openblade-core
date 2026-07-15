using System.Reflection;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryQuerySequenceSessionTests
{
    [Fact]
    public async Task RunAsync_UsesOnePortAndFourOneSecondDelaysBetweenFiveSuccessfulQueries()
    {
        var events = new List<string>();
        using var caller = new CancellationTokenSource();
        var port = new FakePort(events, Enumerable.Repeat(Success(), 5).ToArray());
        var factory = new FakeFactory(events, new(port, null));
        var delayTokens = new List<CancellationToken>();
        var session = new BatteryQuerySequenceSession(
            factory,
            (delay, token) =>
            {
                events.Add($"settle:{delay.TotalMilliseconds:0}");
                delayTokens.Add(token);
                return Task.CompletedTask;
            });

        BatteryQuerySequenceResult result = await session.RunAsync(caller.Token);

        Assert.Equal(
            ["open", "query", "settle:1000", "query", "settle:1000", "query",
             "settle:1000", "query", "settle:1000", "query", "dispose"],
            events);
        Assert.Equal(5, result.CompletedCount);
        Assert.True(result.Success);
        Assert.Equal(4, delayTokens.Count);
        Assert.All(delayTokens, token => Assert.Equal(caller.Token, token));
        Assert.Equal([caller.Token], factory.Tokens);
        Assert.Equal(Enumerable.Repeat(caller.Token, 5), port.Tokens);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_StopsImmediatelyAfterFailedQueryWithoutAnotherDelay()
    {
        var events = new List<string>();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            NativeWin32Error: null,
            TransferredByteCount: 91)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
        };
        var port = new FakePort(
            events,
            Success(BatteryValidationSetting.Limit80),
            new(default, HidSessionFailureKind.ProtocolFailure) { Diagnostic = diagnostic },
            Success());
        var session = new BatteryQuerySequenceSession(
            new FakeFactory(events, new(port, null)),
            (delay, token) =>
            {
                events.Add($"settle:{delay.TotalMilliseconds:0}");
                return Task.CompletedTask;
            });

        BatteryQuerySequenceResult result = await session.RunAsync(default);

        Assert.Equal(["open", "query", "settle:1000", "query", "dispose"], events);
        Assert.Equal(2, result.CompletedCount);
        Assert.False(result.Success);
        Assert.Equal(BatteryQuerySequenceFailure.QueryFailed, result.Query2.Failure);
        Assert.Equal(diagnostic, result.Query2.Diagnostic);
        Assert.Equal(BatteryQuerySequenceFailure.NotAttempted, result.Query3.Failure);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationDuringDelayStopsAndDisposesExactlyOnce()
    {
        var events = new List<string>();
        using var caller = new CancellationTokenSource();
        var port = new FakePort(events, Success(), Success());
        var session = new BatteryQuerySequenceSession(
            new FakeFactory(events, new(port, null)),
            (_, token) =>
            {
                events.Add("settle:1000");
                caller.Cancel();
                return Task.FromCanceled(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RunAsync(caller.Token));

        Assert.Equal(["open", "query", "settle:1000", "dispose"], events);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_OpenFailureDoesNotQueryOrDelay()
    {
        var events = new List<string>();
        int delayCount = 0;
        var session = new BatteryQuerySequenceSession(
            new FakeFactory(
                events,
                new(null, HidSessionFailureKind.IdentityBlocked)),
            (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        BatteryQuerySequenceResult result = await session.RunAsync(default);

        Assert.Equal(["open"], events);
        Assert.Equal(0, delayCount);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(BatteryQuerySequenceFailure.WrongTarget, result.Query1.Failure);
        Assert.False(result.Success);
    }

    [Fact]
    public void PublicOrchestrationSurface_HasNoBatterySettingInput()
    {
        Type[] types = [typeof(BatteryQuerySequenceSession), typeof(BatteryQuerySequenceCommand)];

        IEnumerable<Type> inputTypes = types
            .SelectMany(type => type.GetConstructors().Cast<MethodBase>().Concat(type.GetMethods()))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.DoesNotContain(inputTypes, AcceptsBatterySetting);
    }

    private static bool AcceptsBatterySetting(Type type)
    {
        if (type == typeof(BatteryValidationSetting))
        {
            return true;
        }

        if (!typeof(Delegate).IsAssignableFrom(type) || !type.IsGenericType)
        {
            return false;
        }

        return type.GetGenericArguments().Contains(typeof(BatteryValidationSetting));
    }

    private static HidSessionResult<BatteryValidationSetting> Success(
        BatteryValidationSetting setting = BatteryValidationSetting.ProtectionOff) =>
        new(setting, null);

    private sealed class FakeFactory(
        List<string> events,
        HidSessionResult<IBatteryQuerySequencePort> result) : IBatteryQuerySequenceRunFactory
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task<HidSessionResult<IBatteryQuerySequencePort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            events.Add("open");
            Tokens.Add(cancellationToken);
            return Task.FromResult(result);
        }
    }

    private sealed class FakePort(
        List<string> events,
        params HidSessionResult<BatteryValidationSetting>[] results) : IBatteryQuerySequencePort
    {
        private readonly Queue<HidSessionResult<BatteryValidationSetting>> results = new(results);

        public List<CancellationToken> Tokens { get; } = [];
        public int DisposeCount { get; private set; }

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            CancellationToken cancellationToken)
        {
            events.Add("query");
            Tokens.Add(cancellationToken);
            return Task.FromResult(results.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

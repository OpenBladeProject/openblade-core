using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeBatteryRangeTests
{
    private static ExperimentalBladeModel Model => ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
    public static IEnumerable<object[]> Limits() => Enumerable.Range(50, 51).Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task EveryWholePercent_UsesBaselineOneSetterAndIndependentReadback(int percent)
    {
        var session = new Session { Raw = percent == 77 ? (byte)0xD0 : (byte)0xCD };
        var policy = new ExperimentalFeaturesPolicy(Model) { Enabled = true };
        await using var port = Create(session, policy);
        Assert.Equal(Enumerable.Range(50, 51), port.Capabilities.ChargeLimits.Order());
        if (percent == 100)
        {
            // The production UI maps its 100% marker to the typed protection-off operation.
            await port.SetProtectionOffAsync(CancellationToken.None);
        }
        else
        {
            await port.SetChargeLimitAsync(percent, CancellationToken.None);
        }
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
        byte expected = percent == 100 ? (byte)0x50 : (byte)(0x80 | percent);
        Assert.Equal(new byte[] { expected }, session.Commands[1].Arguments.ToArray());
        Assert.Equal(expected, session.Raw);
        string effective = percent == 100 ? "Off" : percent.ToString();
        Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Readback" && item.ConfirmedValue == effective);
    }

    [Theory]
    [InlineData(0x00, false, null)]
    [InlineData(0x50, false, null)]
    [InlineData(0x64, false, null)]
    [InlineData(0x7F, false, null)]
    [InlineData(0xB2, true, 50)]
    [InlineData(0xB3, true, 51)]
    [InlineData(0xE3, true, 99)]
    [InlineData(0xE4, false, null)]
    public async Task Baseline_DecodesFlagAndExactThresholdWithoutInventingALimit(int raw, bool enabled, int? limit)
    {
        var session = new Session { Raw = (byte)raw };
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        ConfirmedBatteryState state = await port.QueryProtectionAsync(CancellationToken.None);
        Assert.Equal(enabled, state.ProtectionEnabled);
        Assert.Equal(limit, state.ChargeLimitPercent);
        Assert.Contains(policy.Snapshot().Observations, item => item.ConfirmedValue!.Contains($"Raw={raw:X2}"));
        Assert.All(session.Commands, command => Assert.Equal(0x92, command.CommandId));
        Assert.Empty(port.Capabilities.ChargeLimits);
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0xB1)]
    [InlineData(0xE5)]
    [InlineData(0xFF)]
    public async Task UnsupportedEnabledBaseline_BlocksEverySetterAndRetainsRawEvidence(int raw)
    {
        var session = new Session { Raw = (byte)raw };
        var policy = new ExperimentalFeaturesPolicy(Model) { Enabled = true };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(99, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.Equal(0x92, command.CommandId));
        Assert.Contains(policy.Snapshot().Observations, item => item.ConfirmedValue?.Contains($"Raw={raw:X2}") == true);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(101)]
    [InlineData(-1)]
    [InlineData(255)]
    public async Task OutOfRangeRequest_IsRejectedBeforeAnyTraffic(int percent)
    {
        var session = new Session();
        await using var port = Create(session, new(Model) { Enabled = true });
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(percent, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task ConsentOff_BlocksRangeWritesBeforeBaseline(int percent)
    {
        var session = new Session();
        await using var port = Create(session, new(Model));
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(percent, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task EchoWithDifferentEnabledLimit_LatchesRecoveryWithoutReplayingSetter()
    {
        var session = new Session { Raw = 0xC6, IgnoreSetter = true };
        var policy = new ExperimentalFeaturesPolicy(Model) { Enabled = true };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(99, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetProtectionOffAsync(CancellationToken.None));
        Assert.Equal(before, session.Commands.Count);
    }

    [Fact]
    public async Task RevocationAtNativeBoundary_BlocksRangeSetterWithoutSubmission()
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(Model) { Enabled = true };
        session.BeforeSubmission = command => { if (command.CommandId == 0x12) { policy.Enabled = false; } };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(99, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task OtherExperimentalModels_RetainOnlyEightyPercent()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All.Where(item => item.ProductId != 0x02A0))
        {
            var session = new Session();
            var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
            await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true, () => PowerSource.ExternalUnknown);
            Assert.DoesNotContain(99, port.Capabilities.ChargeLimits);
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(99, CancellationToken.None));
            Assert.Empty(session.Commands);
        }
    }

    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy policy) =>
        new(session, new(), Model, policy, () => true, () => PowerSource.ExternalUnknown);

    private sealed class Session : IRazerCommandSession
    {
        internal byte Raw { get; set; } = 0xD0;
        internal bool IgnoreSetter { get; init; }
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                { Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null) });
            }
            Assert.Equal(0x07, command.ClassId);
            Commands.Add(command);
            _ = assign();
            if (command.CommandId == 0x92)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { Raw }, null));
            }
            Assert.Equal(0x12, command.CommandId);
            byte value = command.Arguments.Span[0];
            if (!IgnoreSetter)
            {
                Raw = value;
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { value }, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

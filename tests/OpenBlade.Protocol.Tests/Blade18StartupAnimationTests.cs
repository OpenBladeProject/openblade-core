using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18StartupAnimationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAnimation_ReadGatesCapabilityAndTypedRoundTripConfirmsState(bool profileReply)
    {
        var session = new Session { IncludeProfile = profileReply };
        await using var port = Create(session);
        Assert.DoesNotContain(OneShotLaptopControl.StartupAnimation, port.Capabilities.OneShotLaptopControls);
        Assert.True(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.Contains(OneShotLaptopControl.StartupAnimation, port.Capabilities.OneShotLaptopControls);

        await port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None);
        Assert.False(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 0, 1 }, Assert.Single(session.Writes).Arguments.ToArray());
        await port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, true), CancellationToken.None);
        Assert.True(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 0, 0 }, session.Writes.Last().Arguments.ToArray());
        Assert.All(session.Commands.Where(command => command.CommandId == 0x98), command =>
            Assert.Equal(new byte[] { 0 }, command.Arguments.ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("02")]
    [InlineData("0100")]
    [InlineData("0002")]
    [InlineData("000000")]
    public async Task StartupAnimation_UnknownReadbackDoesNotAdmitOrWrite(string hex)
    {
        var session = new Session { ReplyOverride = Convert.FromHexString(hex) };
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        Assert.Null(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.DoesNotContain(OneShotLaptopControl.StartupAnimation, port.Capabilities.OneShotLaptopControls);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task StartupAnimation_ReadFailureKeepsProvenCapabilityButBlocksMutation()
    {
        var session = new Session();
        await using var port = Create(session);
        Assert.True(await port.QueryStartupAnimationAsync(CancellationToken.None));
        session.ReplyOverride = [2];
        Assert.Null(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.Contains(OneShotLaptopControl.StartupAnimation, port.Capabilities.OneShotLaptopControls);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.ApplyOneShotLaptopControlAsync(
            new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task StartupAnimation_IgnoredWritePausesWithoutReplay()
    {
        var session = new Session { IgnoreSet = true };
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Writes);
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task StartupAnimation_AmbiguousWriteMustBeReadBeforeGlobalRecovery()
    {
        var session = new Session { FailSet = true };
        var policy = new ExperimentalFeaturesPolicy(Model);
        var state = new ExperimentalStartupAnimationState();
        await using var port = new ExperimentalBladePort(session, new(), Model, policy, () => true,
            () => PowerSource.PluggedIn, sharedStartupAnimationState: state);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.ApplyOneShotLaptopControlAsync(
            new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, state.ReconciliationRequired);

        session.ReplyOverride = [2];
        await Assert.ThrowsAsync<IOException>(() => port.ReadRecoveryStateAsync(CancellationToken.None));
        Assert.Equal(1, state.ReconciliationRequired);
        Assert.All(session.Commands, command => Assert.Equal((byte)0x0F, command.ClassId));
        Assert.Single(session.Writes);

        session.ReplyOverride = [0];
        Assert.True(await port.QueryStartupAnimationAsync(CancellationToken.None));
        Assert.Equal(0, state.ReconciliationRequired);
        Assert.False(policy.WritesAllowed); // A read alone must not resume global writes.
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task StartupAnimation_FreshIdentityLossBlocksBeforeSet()
    {
        bool present = true;
        var session = new Session { AfterGet = () => present = false };
        await using var port = Create(session, present: () => present);
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, false), CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task StartupAnimation_AlreadyMatchingDoesNotWrite()
    {
        var session = new Session();
        await using var port = Create(session);
        await port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, true), CancellationToken.None);
        Assert.Empty(session.Writes);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task StartupAnimation_DoesNotProbeOtherModelsOrAcceptAnotherControl()
    {
        var session = new Session();
        ExperimentalBladeModel other = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x029F);
        await using var port = new ExperimentalBladePort(session, new(), other, new(other), () => true,
            () => PowerSource.PluggedIn);
        Assert.Null(await port.QueryStartupAnimationAsync(CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.ApplyOneShotLaptopControlAsync(new(OneShotLaptopControl.StartupAnimation, true), CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    private static ExperimentalBladeModel Model => ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);

    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy? policy = null,
        Func<bool>? present = null) => new(session, new(), Model, policy ?? new(Model), present ?? (() => true),
            () => PowerSource.PluggedIn);

    private sealed class Session : IRazerCommandSession
    {
        internal byte Disable { get; set; }
        internal bool IncludeProfile { get; init; }
        internal byte[]? ReplyOverride { get; set; }
        internal bool IgnoreSet { get; init; }
        internal bool FailSet { get; set; }
        internal Action? AfterGet { get; init; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(command => command.CommandId == 0x18);
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            Commands.Add(command);
            byte[] reply;
            if (command.ClassId == 0x0F && command.CommandId == 0x98)
            {
                reply = ReplyOverride ?? (IncludeProfile ? new byte[] { 0, Disable } : [Disable]);
                AfterGet?.Invoke();
            }
            else if (command.ClassId == 0x0F && command.CommandId == 0x18)
            {
                if (FailSet)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
                }
                if (!IgnoreSet)
                {
                    Disable = command.Arguments.Span[1];
                }
                reply = []; // Source ACK need not echo; only GET confirms the state.
            }
            else
            {
                throw new InvalidOperationException("Unexpected command.");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(reply, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

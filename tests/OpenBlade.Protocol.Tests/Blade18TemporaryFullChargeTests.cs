using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18TemporaryFullChargeTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task FullCharge_ReadGatesCapabilityAndRoundTripPreservesPolicyWithoutThermalCommands(PowerSource source)
    {
        var session = new Session();
        await using var port = Create(session, source: source);
        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        Assert.False(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
        await port.SetTemporaryFullChargeAsync(true, CancellationToken.None);
        Assert.True(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        await port.SetTemporaryFullChargeAsync(false, CancellationToken.None);
        Assert.False(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 4, 0 }, session.Writes.Select(command => command.Arguments.Span[0]));
        Assert.All(session.Commands, command => Assert.True(command.ClassId == 7 && command.CommandId is 0x92 or 0x8F or 0x0F));
        Assert.Equal(0xD0, session.PolicyRaw);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(11)]
    public async Task FullCharge_OtherSharedBitsBlockWholeByteReplacement(byte otherBits)
    {
        var session = new Session { Flags = otherBits };
        await using var port = Create(session);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        session.Flags = (byte)(otherBits | 4);
        failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(false, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("0000")]
    public async Task FullCharge_InvalidReadbackDoesNotAdmitOrWrite(string hex)
    {
        var session = new Session { MaskReply = Convert.FromHexString(hex) };
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        Assert.Null(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task FullCharge_ProtectionOffBlocksEnableButAllowsCancelAndPreservesRawPolicy()
    {
        var session = new Session { PolicyRaw = 73 };
        await using var port = Create(session);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        session.Flags = 4;
        await port.SetTemporaryFullChargeAsync(false, CancellationToken.None);
        Assert.Equal(73, session.PolicyRaw);
        Assert.Equal(0, Assert.Single(session.Writes).Arguments.Span[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCharge_AlreadyMatchingDoesNotWrite(bool active)
    {
        var session = new Session { Flags = active ? (byte)4 : (byte)0 };
        await using var port = Create(session);
        await port.SetTemporaryFullChargeAsync(active, CancellationToken.None);
        Assert.Empty(session.Writes);
        Assert.Equal(new byte[] { 0x92, 0x8F }, session.Commands.Select(command => command.CommandId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCharge_PostWriteMismatchPausesAndDoesNotReplay(bool policyChanged)
    {
        var session = new Session { IgnoreSet = !policyChanged, PolicyAfterSet = policyChanged ? (byte)0xDA : null };
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task FullCharge_DisabledPolicyRawChangeIsNotHiddenByNormalizedState()
    {
        var session = new Session { PolicyRaw = 73, Flags = 4, PolicyAfterSet = 80 };
        await using var port = Create(session);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(false, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCharge_FreshIdentityOrControllerLossBlocksSubmission(bool atSubmission)
    {
        bool admitted = true;
        var session = new Session();
        if (atSubmission) { session.BeforeSet = () => admitted = false; }
        else { session.AfterMask = () => admitted = false; }
        await using var port = Create(session, present: () => admitted);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCharge_CancellationTracksPossibleSubmission(bool afterSet)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session();
        if (afterSet) { session.AfterSet = cancellation.Cancel; }
        else { session.AfterMask = cancellation.Cancel; }
        var policy = new ExperimentalFeaturesPolicy(Model);
        await using var port = Create(session, policy);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, cancellation.Token));
        Assert.Equal(afterSet ? WriteSubmissionState.Indeterminate : WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(afterSet ? 1 : 0, session.Writes.Count());
        Assert.Equal(!afterSet, policy.WritesAllowed);
    }

    [Fact]
    public async Task FullCharge_RecoveryReadsRealFlagAndPolicyBeforeResuming()
    {
        var state = new ExperimentalTemporaryFullChargeState();
        var policy = new ExperimentalFeaturesPolicy(Model);
        var session = new Session { FailSet = true };
        await using var port = Create(session, policy, state);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.False(policy.WritesAllowed);
        session.MaskReply = [16];
        await Assert.ThrowsAsync<IOException>(() => port.ReadRecoveryStateAsync(CancellationToken.None));
        session.MaskReply = null;
        Assert.True(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        var recovered = await port.ReadRecoveryStateAsync(CancellationToken.None);
        Assert.True(recovered.Battery!.TemporaryFullCharge);
        Assert.True(recovered.Battery.ProtectionEnabled);
        Assert.Equal(80, recovered.Battery.ChargeLimitPercent);
        Assert.False(policy.WritesAllowed); // Only factory recovery resumes all writes.
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task FullCharge_ActiveOverrideBlocksDirectChargePolicyChange()
    {
        var session = new Session { Flags = 4 };
        await using var port = Create(session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(70, CancellationToken.None));
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetProtectionOffAsync(CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task FullCharge_OtherModelsDoNotQueryOrWrite()
    {
        var session = new Session();
        var other = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x029F);
        await using var port = new ExperimentalBladePort(session, new(), other, new(other), () => true, () => PowerSource.PluggedIn);
        Assert.Null(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.ExternalUnknown)]
    [InlineData((PowerSource)99)]
    public async Task FullCharge_UnconfirmedExternalPowerBlocksEnableButAllowsCancel(PowerSource source)
    {
        var session = new Session();
        await using var port = Create(session, source: source);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        session.Flags = 4;
        await port.SetTemporaryFullChargeAsync(false, CancellationToken.None);
        Assert.Equal(0, Assert.Single(session.Writes).Arguments.Span[0]);
    }

    [Fact]
    public async Task FullCharge_PowerLossAtSubmissionBlocksEnable()
    {
        PowerSource source = PowerSource.PluggedIn;
        var session = new Session { BeforeSet = () => source = PowerSource.Battery };
        await using var port = new ExperimentalBladePort(session, new(), Model, new(Model), () => true, () => source);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetTemporaryFullChargeAsync(true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    private static ExperimentalBladeModel Model => ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy? policy = null,
        ExperimentalTemporaryFullChargeState? state = null, Func<bool>? present = null, PowerSource source = PowerSource.PluggedIn) =>
        new(session, new(), Model, policy ?? new(Model), present ?? (() => true), () => source,
            sharedTemporaryFullChargeState: state);

    private sealed class Session : IRazerCommandSession
    {
        internal byte Flags { get; set; }
        internal byte PolicyRaw { get; set; } = 0xD0;
        internal byte[]? MaskReply { get; set; }
        internal byte? PolicyAfterSet { get; init; }
        internal bool IgnoreSet { get; init; }
        internal bool FailSet { get; init; }
        internal Action? BeforeSet { get; set; }
        internal Action? AfterSet { get; set; }
        internal Action? AfterMask { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(command => command.CommandId < 0x80);
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (command.CommandId == 0x0F) { BeforeSet?.Invoke(); }
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                { Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null) });
            }
            _ = assignTransactionId();
            Commands.Add(command);
            byte[] response;
            switch ((command.ClassId, command.CommandId))
            {
                case (7, 0x92): response = [PolicyRaw]; break;
                case (7, 0x8F): response = MaskReply ?? [Flags]; AfterMask?.Invoke(); break;
                case (7, 0x0F):
                    if (!IgnoreSet) { Flags = command.Arguments.Span[0]; }
                    if (PolicyAfterSet is { } policy) { PolicyRaw = policy; }
                    AfterSet?.Invoke();
                    if (FailSet) { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)); }
                    response = []; break; // Do not infer state from vendor ACK.
                case (0x0D, 0x82): response = [0, command.Arguments.Span[1], 0, 0]; break;
                case (3, 0x80): response = command.Arguments.ToArray(); break;
                default: throw new InvalidOperationException($"Unexpected {command.ClassId:X2}/{command.CommandId:X2}");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

// Each admitted model shares the 07/8F and 07/0F contract; derived classes pin the exact product.
public abstract class SourceReviewedTemporaryFullChargeTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task FullCharge_StableCapabilityAndRoundTripPreservePolicyWithoutThermalCommands(PowerSource source)
    {
        var session = new Session();
        await using var port = Create(session, source: source);
        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
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
        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
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
        var policy = new ExperimentalFeaturesPolicy(Model);
        var session = new Session { FailSet = true };
        await using var port = Create(session, policy);
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

    protected abstract ushort ProductId { get; }
    private ExperimentalBladeModel Model => ExperimentalBladeModels.All.Single(item => item.ProductId == ProductId);
    private ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy? policy = null,
        Func<bool>? present = null, PowerSource source = PowerSource.PluggedIn) =>
        new(session, new(), Model, policy ?? new(Model), present ?? (() => true), () => source);

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
                case (0x0D, 0x82): response = [command.Arguments.Span[0], command.Arguments.Span[1], 0, 0]; break;
                case (3, 0x80): response = command.Arguments.ToArray(); break;
                // RZ09-0529 lighting recovery reads zero brightness, the Off effect and an off indicator.
                case (3, 0x83) or (0x0F, 0x82) or (0, 0xD9): response = command.Arguments.ToArray(); break;
                default: throw new InvalidOperationException($"Unexpected {command.ClassId:X2}/{command.CommandId:X2}");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class Blade18TemporaryFullChargeTests : SourceReviewedTemporaryFullChargeTests
{
    protected override ushort ProductId => 0x02A0;
}

public sealed class Rz090529TemporaryFullChargeTests : SourceReviewedTemporaryFullChargeTests
{
    protected override ushort ProductId => 0x02C7;

    // Products 709 and 711 each pin their own rzDevice25 and 6737 helpers; no other
    // source-reviewed model inherits the contract.
    [Fact]
    public void Admission_IsLimitedToProductsWithReviewedHelpers()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            bool admitted = model.ProductId is 0x02A0 or 0x02C5 or 0x02C7;
            Assert.Equal(admitted, ExperimentalBladePort.CreateCapabilities(model, enabled: true).SupportsTemporaryFullCharge);
            Assert.False(ExperimentalBladePort.CreateCapabilities(model, enabled: false).SupportsTemporaryFullCharge);
        }
        AssertEvidence(0x02C7,
            "https://apps.razer.com/synapse/products/711/mw/rzDevice25.c8da1ead63e6cf002d32.js;SHA256=02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9",
            "https://apps.razer.com/synapse/products/711/mw/6737.c63928b0e4fd1b7673b4.js;SHA256=3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88",
            "https://apps.razer.com/synapse/products/711/mw/main.1f8e0c604f19af3ea24c.js;SHA256=C8C0C794D6D01867CD9C6020869808428B81CB98AE297E46EC700F3B935BFF7E");
        AssertEvidence(0x02C5,
            "https://apps.razer.com/synapse/products/709/mw/rzDevice25.d532642d8de7a9cfcf5d.js;SHA256=9109826D790FEB8597BBFC115231066CA66E2C480ED9769BB85F3F91188AAD0A",
            "https://apps.razer.com/synapse/products/709/mw/6737.d34a3f9a638030f35c6a.js;SHA256=4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0",
            "https://apps.razer.com/synapse/products/709/mw/main.bb0dd1873a58a987c63a.js;SHA256=CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1");
    }

    // Both admitted backends already accept status-only SET ACKs for their other
    // source-reviewed setters; the pinned 07/0F helpers parse payload only when present.
    [Fact]
    public void Admission_UsesEachModelsReviewedStatusOnlySetAckContract()
    {
        foreach (ushort product in new ushort[] { 0x02C5, 0x02C7 })
        {
            ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
            Assert.True(ExperimentalBladePort.UsesSourceBackedSetAckContract(model));
        }
    }

    private static void AssertEvidence(ushort product, params string[] sources)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
        ExperimentalFeatureEvidence evidence = Assert.Single(ExperimentalBladeModels.FeatureEvidenceFor(model),
            item => item.Feature == "TemporaryFullCharge");
        Assert.Equal("SynapseSourceBackedWithoutPhysicalValidation", evidence.Basis);
        Assert.Equal(sources, evidence.Sources);
    }
}

public sealed class Rz090530TemporaryFullChargeTests : SourceReviewedTemporaryFullChargeTests
{
    protected override ushort ProductId => 0x02C5;
}

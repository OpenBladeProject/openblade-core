using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class LocalDimmingClearTests
{
    [Theory]
    [InlineData(0x00, (int)LocalDimmingClearKindValue.AlreadyClear, 0x00)]
    [InlineData(0x04, (int)LocalDimmingClearKindValue.AlreadyClear, 0x04)]
    [InlineData(0x08, (int)LocalDimmingClearKindValue.Clear, 0x00)]
    [InlineData(0x0C, (int)LocalDimmingClearKindValue.Clear, 0x04)]
    [InlineData(0x09, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    [InlineData(0x0A, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    [InlineData(0x0B, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    [InlineData(0x0F, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    [InlineData(0x18, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    [InlineData(0x80, (int)LocalDimmingClearKindValue.Blocked, 0x00)]
    public void PlanClear_KeepsOnlyFullChargeAndRefusesThermalOrUnknownBits(
        byte flags, int kind, byte value)
    {
        LocalDimmingClearPlan plan = LocalDimmingProtocol.PlanClear(flags);
        Assert.Equal((LocalDimmingClearKind)kind, plan.Kind);
        Assert.Equal(value, plan.Value);
        Assert.Equal(plan.Kind == LocalDimmingClearKind.Blocked, plan.Refusal is not null);
    }

    [Theory]
    [InlineData(0x0A, "Turn off Maximum Fans in Synapse")]
    [InlineData(0x0E, "Turn off Maximum Fans in Synapse")]
    [InlineData(0x09, "CPU boost is on. Turn it off in Synapse")]
    [InlineData(0x0B, "Maximum Fans and Synapse’s CPU boost are on. Turn them off in Synapse")]
    public void PlanClear_SaysWhichSettingToTurnOffInSynapse(byte flags, string expected)
    {
        string refusal = LocalDimmingProtocol.PlanClear(flags).Refusal!;
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("02", true, true)]
    [InlineData("03", true, true)]
    [InlineData("07", true, true)]
    [InlineData("00", true, false)]
    [InlineData("01", true, false)]
    [InlineData("04", true, false)]
    [InlineData("08", false, false)]
    [InlineData("0A", false, false)]
    [InlineData("FF", false, false)]
    [InlineData("", false, false)]
    [InlineData("0200", false, false)]
    public void TryDecodeMiniLedPanel_ReadsOnlyTheMiniLedBitOfOneByte(string hex, bool decoded, bool miniLed)
    {
        Assert.Equal(decoded, LocalDimmingProtocol.TryDecodeMiniLedPanel(Convert.FromHexString(hex), out bool panel));
        Assert.Equal(miniLed, panel);
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task MiniLedPanel_OffersClearAndReportsTheLocalDimmingBit(int productId)
    {
        var session = new Session { Flags = 0x08 };
        var policy = new ExperimentalFeaturesPolicy(Model(productId));
        await using var port = Create(productId, session, policy);
        Assert.Null(port.Capabilities.LocalDimming);
        Assert.False(port.SupportsLocalDimmingQuery);

        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);

        Assert.True(policy.MiniLedPanel);
        Assert.Equal(new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
            port.Capabilities.LocalDimming);
        Assert.True(port.Capabilities.LocalDimming!.IsWellFormed);
        Assert.True(port.SupportsLocalDimmingQuery);
        Assert.True(await port.QueryLocalDimmingAsync(CancellationToken.None));
        session.Flags = 0x04;
        Assert.False(await port.QueryLocalDimmingAsync(CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task PanelWithoutMiniLed_DoesNotOfferOrWriteClear(int productId)
    {
        var session = new Session { Flags = 0x08, Panel = [0x01] };
        var policy = new ExperimentalFeaturesPolicy(Model(productId));
        await using var port = Create(productId, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);

        Assert.False(policy.MiniLedPanel);
        Assert.Null(port.Capabilities.LocalDimming);
        Assert.False(port.SupportsLocalDimmingQuery);
        Assert.Null(await port.QueryLocalDimmingAsync(CancellationToken.None));
        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.DoesNotContain(session.Commands, command => command is { ClassId: 7, CommandId: 0x8F });
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0200")]
    public async Task UnreadablePanel_LeavesClearUnofferedWithoutPausingWrites(string hex)
    {
        var session = new Session { Flags = 0x08, Panel = Convert.FromHexString(hex) };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);

        Assert.Null(policy.MiniLedPanel);
        Assert.Null(port.Capabilities.LocalDimming);
        Assert.False(port.SupportsLocalDimmingQuery);
        Assert.True(policy.WritesAllowed);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x08, 0x00)]
    [InlineData(0x0C, 0x04)]
    public async Task Clear_ReadsPanelAndMaskFreshThenWritesOnlyTheExactValue(byte flags, byte written)
    {
        var session = new Session { Flags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        session.Commands.Clear();

        await port.ClearLocalDimmingAsync(CancellationToken.None);

        Assert.Equal(
            [(0x0D, 0x8F), (0x07, 0x8F), (0x07, 0x0F), (0x07, 0x8F)],
            session.Commands.Select(command => ((int)command.ClassId, (int)command.CommandId)));
        Assert.Equal(written, Assert.Single(session.Writes).Arguments.Span[0]);
        Assert.Equal(written, session.Flags);
        Assert.True(policy.WritesAllowed);
        Assert.False(await port.QueryLocalDimmingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Clear_PanelChangedSinceOpenIsReadFreshAndRefused()
    {
        var session = new Session { Flags = 0x08 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B8));
        await using var port = Create(0x02B8, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        session.Panel = [0x00];

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.False(policy.MiniLedPanel);
        Assert.Null(port.Capabilities.LocalDimming);
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    [InlineData(0x0D)]
    [InlineData(0x0E)]
    public async Task Clear_RefusesWhileCpuBoostOrMaximumFansIsOn(byte flags)
    {
        var session = new Session { Flags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy);

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(flags, session.Flags);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x04)]
    public async Task Clear_AlreadyClearDoesNotWrite(byte flags)
    {
        var session = new Session { Flags = flags };
        await using var port = Create(0x02B7, session);
        await port.ClearLocalDimmingAsync(CancellationToken.None);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Clear_ReadbackMismatchPausesWritesAndIsNotReplayed()
    {
        var session = new Session { Flags = 0x08, IgnoreSet = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: false),
            port.Capabilities.LocalDimming);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Single(session.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_AmbiguousSubmissionPausesWritesAndIsNotReplayed(bool canceledAfterSet)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { Flags = 0x08, FailSet = !canceledAfterSet };
        if (canceledAfterSet) { session.AfterSet = cancellation.Cancel; }
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B8));
        await using var port = Create(0x02B8, session, policy);

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(cancellation.Token));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Single(session.Writes);
    }

    // The session would submit anyway: only the port's second admission check,
    // after its reads, stops the write.
    [Fact]
    public async Task Clear_AdmitsAgainAfterItsReadsBeforeSubmitting()
    {
        bool admitted = true;
        var session = new Session { Flags = 0x08, AfterMask = () => admitted = false, IgnoreSubmissionGuard = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy, () => admitted);

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task PanelReading_IsCachedAndAFailedReadIsNotRepeated()
    {
        var session = new Session { Flags = 0x08 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using (var first = Create(0x02B7, session, policy))
        {
            await first.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        }
        session.Panel = [];
        await using (var second = Create(0x02B7, session, policy))
        {
            await second.InitializeLocalDimmingPanelAsync(CancellationToken.None);
            Assert.True(second.SupportsLocalDimmingQuery);
        }
        Assert.Single(session.Commands, command => command is { ClassId: 0x0D, CommandId: 0x8F });

        var unreadPolicy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        session.Commands.Clear();
        await using (var failed = Create(0x02B7, session, unreadPolicy))
        {
            await failed.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        }
        session.Panel = [0x02];
        await using (var later = Create(0x02B7, session, unreadPolicy))
        {
            await later.InitializeLocalDimmingPanelAsync(CancellationToken.None);
            Assert.Null(later.Capabilities.LocalDimming);
        }
        Assert.Single(session.Commands, command => command is { ClassId: 0x0D, CommandId: 0x8F });
        Assert.True(unreadPolicy.MiniLedPanelResolved);
        Assert.Null(unreadPolicy.MiniLedPanel);
    }

    [Fact]
    public async Task QueryFailure_IsRememberedUntilPowerModeControlIsReadAgain()
    {
        var session = new Session { Flags = 0x08 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B8));
        await using var port = Create(0x02B8, session, policy);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        session.FailMask = true;

        await Assert.ThrowsAnyAsync<Exception>(() => port.QueryLocalDimmingAsync(CancellationToken.None));

        Assert.True(policy.LocalDimmingReadUnavailable);
        Assert.False(port.SupportsLocalDimmingQuery);
        Assert.Null(await port.QueryLocalDimmingAsync(CancellationToken.None));
        session.FailMask = false;
        await port.ClearLocalDimmingAsync(CancellationToken.None);
        Assert.False(policy.LocalDimmingReadUnavailable);
        Assert.False(await port.QueryLocalDimmingAsync(CancellationToken.None));
    }

    [Fact]
    public void RecoveryObligation_FollowsOnlyAPossibleClearSubmission()
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        policy.RequireRecovery();
        Assert.False(policy.LocalDimmingRecoveryRequired);
        Assert.True(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All, () => true, localDimmingObserved: false));

        policy.RequireRecovery(localDimming: true);
        // A later unrelated fault keeps the obligation.
        policy.RequireRecovery();
        Assert.True(policy.LocalDimmingRecoveryRequired);
        bool accepted = false;
        Assert.False(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All,
            () => accepted = true, localDimmingObserved: false));
        Assert.False(accepted);
        Assert.True(policy.IsPaused);

        Assert.True(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All, () => true, localDimmingObserved: true));
        Assert.False(policy.LocalDimmingRecoveryRequired);
        Assert.False(policy.IsPaused);
    }

    [Fact]
    public async Task Clear_HostLossBeforeSubmissionDoesNotWrite()
    {
        bool admitted = true;
        var session = new Session { Flags = 0x08, AfterMask = () => admitted = false };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = Create(0x02B7, session, policy, () => admitted);

        var failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    // Clear changes no thermal bit after the guard, so the thermal external-power
    // gate does not apply; general write admission and the guard still do.
    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.ExternalUnknown)]
    public async Task Clear_DoesNotRequireClassifiedExternalPower(PowerSource source)
    {
        var session = new Session { Flags = 0x08 };
        await using var port = Create(0x02B7, session, source: source);
        await port.ClearLocalDimmingAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(session.Writes).Arguments.Span[0]);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    [InlineData(0x02B6)]
    public async Task OtherModels_NeverReadThePanelOrWriteClear(int productId)
    {
        var session = new Session { Flags = 0x08 };
        await using var port = Create(productId, session);
        await port.InitializeLocalDimmingPanelAsync(CancellationToken.None);
        Assert.False(port.SupportsLocalDimmingQuery);
        Assert.Null(await port.QueryLocalDimmingAsync(CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Empty(session.Commands);
        Assert.NotEqual(true, port.Capabilities.LocalDimming?.SupportsClear);
    }

    [Fact]
    public void OnlyRz090510AndRz090509AdmitClear()
    {
        Assert.Equal([0x02B7, 0x02B8],
            ExperimentalBladeModels.All.Where(ExperimentalBladePort.SupportsLocalDimmingClear)
                .Select(model => (int)model.ProductId).Order().ToArray());
    }

    // Mirrors LocalDimmingClearKind so public theories need no internal parameter type.
    private static class LocalDimmingClearKindValue
    {
        internal const int AlreadyClear = (int)LocalDimmingClearKind.AlreadyClear;
        internal const int Blocked = (int)LocalDimmingClearKind.Blocked;
        internal const int Clear = (int)LocalDimmingClearKind.Clear;
    }

    private static ExperimentalBladeModel Model(int productId) =>
        ExperimentalBladeModels.All.Single(item => item.ProductId == productId);

    private static ExperimentalBladePort Create(int productId, Session session,
        ExperimentalFeaturesPolicy? policy = null, Func<bool>? present = null,
        PowerSource source = PowerSource.PluggedIn)
    {
        ExperimentalBladeModel model = Model(productId);
        return new(session, new(), model, policy ?? new(model), present ?? (() => true), () => source);
    }

    private sealed class Session : IRazerCommandSession
    {
        internal byte Flags { get; set; }
        internal byte[] Panel { get; set; } = [0x02];
        internal bool IgnoreSet { get; init; }
        internal bool FailSet { get; init; }
        internal Action? AfterSet { get; set; }
        internal Action? AfterMask { get; set; }
        internal bool FailMask { get; set; }
        internal bool IgnoreSubmissionGuard { get; init; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(command => command.CommandId < 0x80);
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IgnoreSubmissionGuard && command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                { Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null) });
            }
            _ = assignTransactionId();
            Commands.Add(command);
            byte[] response;
            switch ((command.ClassId, command.CommandId))
            {
                case (0x0D, 0x8F): response = Panel; break;
                case (7, 0x8F):
                    if (FailMask) { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)); }
                    response = [Flags]; AfterMask?.Invoke(); break;
                case (7, 0x0F):
                    if (!IgnoreSet) { Flags = command.Arguments.Span[0]; }
                    AfterSet?.Invoke();
                    if (FailSet) { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)); }
                    response = []; break;
                default: throw new InvalidOperationException($"Unexpected {command.ClassId:X2}/{command.CommandId:X2}");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

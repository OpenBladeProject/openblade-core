using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladePortTests
{
    [Fact]
    public async Task ConsentDisabled_DoesNotSubmitQueriesOrWritesFromSetter()
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(ExperimentalBladeModels.All[0]);
        await using var port = Create(session, policy);
        Assert.Empty(port.Capabilities.PerformancePresets);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task UnsupportedBatteryRead_RecordsDecodedEvidenceWithoutEnablingWrites()
    {
        var session = new Session([0xBC]);
        var policy = Enabled();
        await using var port = Create(session, policy);

        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(CancellationToken.None));

        ExperimentalControlObservation observation = Assert.Single(policy.Snapshot().Observations);
        Assert.Equal("UnsupportedState", observation.Outcome);
        Assert.Contains("ReportedLimitPercent=60", observation.ConfirmedValue);
        Assert.False(observation.Submitted);
        Assert.All(session.Commands, command => Assert.Equal(0x92, command.CommandId));
    }

    [Fact]
    public async Task BatteryWrite_UsesBaselineThenIndependentGet()
    {
        var session = new Session([0x50], [0xD0], [0xD0]);
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.SetChargeLimitAsync(80, CancellationToken.None);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
        Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Readback" && item.ConfirmedValue == "80");
    }

    [Fact]
    public async Task EchoWithoutStateChange_PausesWritesAcrossNewPorts()
    {
        var session = new Session([0x50], [0xD0], [0x50], [0x50], [0x50], [0x50],
            [0x50], [0x50], [0x50], [0x50], [0x50]);
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.Commands.Count(command => command.CommandId == 0x12));
        Assert.Equal(10, session.Commands.Count(command => command.CommandId == 0x92));
        var secondSession = new Session();
        await using var second = Create(secondSession, policy);
        await Assert.ThrowsAsync<BladeWriteException>(() => second.SetProtectionOffAsync(CancellationToken.None));
        Assert.Empty(secondSession.Commands);
    }

    [Fact]
    public async Task BatteryReadbackConvergesAfterStaleReads_WithoutReplayingSetter()
    {
        var session = new Session([0x50], [0xD0], [0x50], [0x50], [0xD0]);
        var policy = Enabled();
        int delays = 0;
        await using var port = Create(session, policy, (delay, token) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
            delays++;
            return Task.CompletedTask;
        });
        await port.SetChargeLimitAsync(80, CancellationToken.None);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(2, delays);
        Assert.Equal(1, session.Commands.Count(command => command.CommandId == 0x12));
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x92));
    }

    [Fact]
    public async Task CancellationDuringBatteryConvergence_PausesWithoutAnotherSetOrGet()
    {
        var session = new Session([0x50], [0xD0], [0x50]);
        var policy = Enabled();
        using var cancellation = new CancellationTokenSource();
        await using var port = Create(session, policy, (delay, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, cancellation.Token));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.Commands.Count(command => command.CommandId == 0x12));
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x92));
    }

    [Fact]
    public async Task InvalidBatteryReadback_StopsImmediatelyWithoutRetry()
    {
        var session = new Session([0x50], [0xD0], [0xFF]);
        var policy = Enabled();
        int delays = 0;
        await using var port = Create(session, policy, (delay, token) =>
        {
            delays++;
            return Task.CompletedTask;
        });
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(0, delays);
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task FirstSetterTimeout_IsIndeterminateAndPausesFutureWrites()
    {
        var session = new Session([0x50]);
        session.FailWrites = true;
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.Commands.Count);
    }

    [Fact]
    public async Task ConsentRevokedAfterBaseline_BlocksSetter()
    {
        var session = new Session([0x50]);
        var policy = Enabled();
        session.AfterCommand = command => policy.Enabled = false;
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ConsentRevokedAtSubmissionBoundary_DoesNotSendSetter()
    {
        var session = new Session([0x50]);
        var policy = Enabled();
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x12)
            {
                policy.Enabled = false;
            }
        };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ExactIdentityLost_BlocksWrite()
    {
        var session = new Session();
        await using var port = new ExperimentalBladePort(session, new(), ExperimentalBladeModels.All[0],
            Enabled(), () => false, () => PowerSource.ExternalUnknown);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetProtectionOffAsync(CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task PerformanceWrite_ConfirmsBothSelectors()
    {
        var session = new Session([0, 1, 0, 0], [0, 2, 0, 0],
            [1, 1, 5, 0], [1, 2, 5, 0], [0, 1, 5, 0], [0, 2, 5, 0]);
        await using var port = Create(session, Enabled());
        await port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None);
        Assert.Equal(new byte[] { 0x82, 0x82, 0x02, 0x02, 0x82, 0x82 },
            session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task PerformanceSelectorDisagreement_RecordsBothReportedModes()
    {
        var session = new Session([0, 1, 0, 0], [0, 2, 5, 0]);
        var policy = Enabled();
        await using var port = Create(session, policy);

        await Assert.ThrowsAsync<IOException>(() => port.QueryPerformanceFanStateAsync(CancellationToken.None));

        ExperimentalControlObservation observation = Assert.Single(policy.Snapshot().Observations);
        Assert.Equal("InconsistentState", observation.Outcome);
        Assert.Contains("Mode1=0", observation.ConfirmedValue);
        Assert.Contains("Mode2=5", observation.ConfirmedValue);
        Assert.False(observation.Submitted);
    }

    [Fact]
    public async Task PerformanceSelectorDisagreement_FailsBeforeWriting()
    {
        var session = new Session([0, 1, 0, 0], [0, 2, 5, 0]);
        await using var port = Create(session, Enabled());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task PerformanceOnBattery_IsBlockedBeforeQueryOrWrite()
    {
        var session = new Session();
        await using var port = new ExperimentalBladePort(session, new(), ExperimentalBladeModels.All[0],
            Enabled(), () => true, () => PowerSource.Battery);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Blade2025BatteryModeBaseline_DoesNotReceiveAcPreset()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var session = new Session([0, 1, 6, 0], [0, 2, 6, 0]);
        var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
        await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true,
            () => PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task ExternalPowerObservation_DoesNotClaimAdapterWattage()
    {
        await using var port = Create(new Session(), Enabled());
        Assert.Equal(AdapterPowerState.ExperimentalExternal,
            await port.QueryAdapterPowerStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Blade2020_HasNoClass07BatteryCommands()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x0255);
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
        await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true,
            () => PowerSource.ExternalUnknown);
        Assert.False(port.SupportsProtectionQuery);
        Assert.Empty(port.Capabilities.ChargeLimits);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public void ObservationHistory_IsBounded()
    {
        var policy = Enabled();
        for (int index = 0; index < 40; index++)
        {
            policy.Record("Performance", "Baseline", "Confirmed");
        }
        Assert.Equal(32, policy.Snapshot().Observations.Count);
        Assert.Equal("UpstreamDerived", policy.Snapshot().EvidenceLevel);
    }

    [Fact]
    public void UnchangedBackgroundQueries_DoNotEvictWriteEvidence()
    {
        var policy = Enabled();
        policy.Record("Performance", "Readback", "Confirmed", true, "Silent", "Silent");
        for (int index = 0; index < 100; index++)
        {
            policy.Record("Performance", "Query", "Confirmed", confirmed: "Silent/Automatic");
            policy.Record("BatteryProtection", "Query", "Confirmed", confirmed: "80");
        }
        Assert.Equal(3, policy.Snapshot().Observations.Count);
        Assert.Equal("Readback", policy.Snapshot().Observations[0].Stage);
    }

    [Fact]
    public void RepeatedQueryFailures_DoNotEvictWriteEvidence()
    {
        var policy = Enabled();
        policy.Record("Performance", "Readback", "Confirmed", true, "Silent", "Silent");
        for (int index = 0; index < 100; index++)
        {
            policy.Record("Performance", "Query", "Failed", failure: "Timeout");
        }
        Assert.Equal(2, policy.Snapshot().Observations.Count);
        policy.Record("Performance", "Query", "Confirmed", confirmed: "Silent/Automatic");
        policy.Record("Performance", "Query", "Failed", failure: "Timeout");
        Assert.Equal(4, policy.Snapshot().Observations.Count);
        Assert.Equal("Readback", policy.Snapshot().Observations[0].Stage);
    }

    [Fact]
    public async Task UnsupportedPerformanceMode_RecordsDecodedEvidenceAndBlocksMutation()
    {
        var session = new Session([0, 1, 99, 0], [0, 2, 99, 0]);
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Contains(policy.Snapshot().Observations, item => item.ConfirmedValue == "ReportedMode=99");
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Blade18_UsesItsUpstreamModesAndConfirmsSilent(int initialMode)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        byte[][] baseline = initialMode == 4
            ? [[0, 1, 4, 0], [0, 2, 4, 0], [0, 1, 0], [0, 2, 0]]
            : [[0, 1, (byte)initialMode, 0], [0, 2, (byte)initialMode, 0]];
        var session = new Session([.. baseline, [1, 1, 5, 0], [1, 2, 5, 0], [0, 1, 5, 0], [0, 2, 5, 0]]);
        var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
        await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true,
            () => PowerSource.ExternalUnknown);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, port.Capabilities.HyperBoostAccess);
        Assert.Contains(LightingEffect.Spectrum, port.Capabilities.LightingEffects);
        Assert.Contains(LightingEffect.Wave, port.Capabilities.ObservedLightingEffects);
        Assert.Contains(PerformancePreset.Performance, port.Capabilities.PerformancePresets);
        await port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None);
        Assert.Equal(initialMode == 4 ? new byte[] { 0x82, 0x82, 0x87, 0x87, 0x02, 0x02, 0x82, 0x82 }
            : new byte[] { 0x82, 0x82, 0x02, 0x02, 0x82, 0x82 }, session.Commands.Select(item => item.CommandId));
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade18_BatteryUsesIndependentReadbackAndSingleSetter()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var session = new Session([0x50], [0xD0], [0xD0]);
        var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
        await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true,
            () => PowerSource.ExternalUnknown);
        await port.SetChargeLimitAsync(80, CancellationToken.None);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(item => item.CommandId));
    }

    private static ExperimentalFeaturesPolicy Enabled() => new(ExperimentalBladeModels.All[0]) { Enabled = true };

    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy policy,
        Func<TimeSpan, CancellationToken, Task>? confirmationDelay = null) =>
        new(session, new(), ExperimentalBladeModels.All[0], policy, () => true, () => PowerSource.ExternalUnknown,
            confirmationDelay ?? ((_, _) => Task.CompletedTask));

    private sealed class Session(params byte[][] responses) : IRazerCommandSession
    {
        private readonly Queue<byte[]> responses = new(responses);
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal Action<RazerCommandSpec>? AfterCommand { get; set; }
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal bool FailWrites { get; set; }
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default,
                    HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            Commands.Add(command);
            _ = assignTransactionId();
            if (FailWrites && command.CommandId == 0x12)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)
                {
                    Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.Timeout, null, null),
                });
            }
            byte[] payload = responses.Dequeue();
            AfterCommand?.Invoke(command);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

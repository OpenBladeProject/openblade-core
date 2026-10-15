using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalAdapterPowerTests
{
    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    [InlineData(0x02A0)]
    public async Task EveryAdmittedModel_UsesTheSameSynapseQueryAndEvidence(int pid)
    {
        var session = new Session { Adapter = [0x0C, 0x0C] };
        ExperimentalBladeModel model = Model(pid);
        await using var port = Create(session, model);

        Assert.Equal(AdapterPowerState.SufficientExternal,
            await port.QueryAdapterPowerStateAsync(default));

        RazerCommandSpec query = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x8C), (query.ClassId, query.CommandId));
        Assert.Equal([0, 0], query.Arguments.ToArray());
        Assert.Equal(2, query.ExpectedPayloadLength);
        ExperimentalFeatureEvidence evidence = Assert.Single(
            new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence,
            item => item.Feature == "AdapterPowerClassification");
        Assert.Equal("SynapseSourceBacked", evidence.Basis);
        Assert.Contains(model.AdapterPowerEvidenceSource!, evidence.Sources);
    }

    [Theory]
    [InlineData("0C11", AdapterPowerState.LimitedExternal)]
    [InlineData("0F11", AdapterPowerState.SufficientExternal)]
    [InlineData("110F", AdapterPowerState.LimitedExternal)]
    [InlineData("0011", AdapterPowerState.Battery)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    [InlineData("0C10", AdapterPowerState.Unknown)]
    public async Task Query_UsesActualAndRecommendedWatts(string payload, AdapterPowerState expected)
    {
        var session = new Session { Adapter = Convert.FromHexString(payload) };
        await using var port = Create(session);
        Assert.Equal(expected, await port.QueryAdapterPowerStateAsync(default));
        Assert.Equal(expected == AdapterPowerState.Unknown, session.RetainedFailures.Count != 0);
    }

    [Fact]
    public async Task FutureProfile_WithoutVendorEvidenceCannotInheritTheQuery()
    {
        var session = new Session();
        var model = new ExperimentalBladeModel("RZ09-9999", "Unknown Blade", 0x1234, false, "unreviewed");
        await using var port = Create(session, model);
        Assert.Equal(AdapterPowerState.Unknown, await port.QueryAdapterPowerStateAsync(default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("0C11")]
    [InlineData("FFFF")]
    [InlineData("0011")]
    public async Task UnsafePerformanceRequest_IsBlockedBeforeAnySet(string adapter)
    {
        var session = new Session { Adapter = Convert.FromHexString(adapter) };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(session, Model(0x029D), policy);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11")]
    [InlineData("111111")]
    public async Task MalformedAdapterReply_BlocksThermalSetWithoutPausingUnsubmittedWrites(string adapter)
    {
        var session = new Session { Adapter = Convert.FromHexString(adapter) };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(session, Model(0x029D), policy);
        await Assert.ThrowsAsync<IOException>(() => port.QueryAdapterPowerStateAsync(default));
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterBecomesLimitedOrUnknownBetweenSelectors_StopsWithoutReplay(bool unknown)
    {
        var session = new Session();
        session.AfterSet = () => session.Adapter = unknown ? [0xFF, 0xFF] : [0x0C, 0x11];
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(session, Model(0x029D), policy);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Single(session.Writes);
        Assert.False(policy.WritesAllowed);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F, 5)]
    [InlineData(0x029D, 5)]
    [InlineData(0x029F, 4)]
    [InlineData(0x029D, 4)]
    public async Task LimitedPower_AutomaticHandbackPreservesModeBeforeBalanced(int pid, byte mode)
    {
        var session = new Session { Adapter = [0x0C, 0x11], Mode = mode, Fan = 1 };
        await using var port = Create(session, Model(pid));
        await port.SetFanModeAsync(FanMode.Automatic, default);
        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
        Assert.Equal(0, session.Mode);
        Assert.Equal(0, session.Fan);
        Assert.Equal(new[] { $"0101{mode:X2}00", $"0102{mode:X2}00", "01010000", "01020000" },
            session.Writes.Select(item => Convert.ToHexString(item.Arguments.Span)));
        AssertFreshQueryBeforeEverySet(session);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task LimitedPower_LeavingCustomClearsMaximumFansBeforeBalanced(int pid)
    {
        var session = new Session { Adapter = [0x0C, 0x11], Mode = 4, PowerFlags = 2 };
        await using var port = Create(session, Model(pid));
        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
        Assert.Equal(new byte[] { 0x0F, 0x02, 0x02 }, session.Writes.Select(item => item.CommandId));
        Assert.Equal(0, session.PowerFlags);
        Assert.Equal(0, session.Mode);
        AssertFreshQueryBeforeEverySet(session);
    }

    [Fact]
    public async Task MaximumFansCleanup_WithOtherSharedBitsDoesNotWrite()
    {
        var session = new Session { Adapter = [0x0C, 0x11], Mode = 4, PowerFlags = 3 };
        await using var port = Create(session, Model(0x029F));
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(3, session.PowerFlags);
    }

    [Theory]
    [InlineData(0x029F, PerformancePreset.Silent)]
    [InlineData(0x029F, PerformancePreset.Performance)]
    [InlineData(0x02A0, PerformancePreset.Silent)]
    [InlineData(0x02A0, PerformancePreset.Performance)]
    public async Task LimitedPower_UnsupportedDestinationCannotStartMaximumFansCleanup(int pid, PerformancePreset target)
    {
        var session = new Session { Adapter = [0x0C, 0x11], Mode = 4, PowerFlags = 2 };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(session, Model(pid), policy);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(target, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(2, session.PowerFlags);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task AdapterUnknownAfterMaximumFansCleanup_PausesWithoutChangingPresetOrReplaying()
    {
        var session = new Session { Mode = 4, PowerFlags = 2 };
        session.AfterSet = () => session.Adapter = [0xFF, 0xFF];
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = Create(session, Model(0x029F), policy);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal((0x07, 0x0F), (Assert.Single(session.Writes).ClassId, Assert.Single(session.Writes).CommandId));
        Assert.Equal(0, session.PowerFlags);
        Assert.Equal(4, session.Mode);
        Assert.False(policy.WritesAllowed);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Fact]
    public async Task LimitedExternal_IsNotA2025BatteryMode()
    {
        var session = new Session { Adapter = [0x0C, 0x11], Mode = 6 };
        await using var port = Create(session, Model(0x02C5));
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.BatteryBalanced, default));
        Assert.Empty(session.Commands);
        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
        Assert.Equal(0, session.Mode);
        AssertFreshQueryBeforeEverySet(session);
    }

    private static void AssertFreshQueryBeforeEverySet(Session session)
    {
        for (int index = 0; index < session.Commands.Count; index++)
        {
            if (session.Commands[index].CommandId < 0x80)
            {
                Assert.True(index > 0);
                Assert.Equal((0x07, 0x8C),
                    (session.Commands[index - 1].ClassId, session.Commands[index - 1].CommandId));
            }
        }
    }

    private static ExperimentalBladeModel Model(int pid) =>
        ExperimentalBladeModels.All.Single(item => item.ProductId == pid);

    private static ExperimentalBladePort Create(Session session, ExperimentalBladeModel? model = null,
        ExperimentalFeaturesPolicy? policy = null) =>
        new(session, new(), model ?? Model(0x029D), policy ?? new(model ?? Model(0x029D)),
            () => true, () => PowerSource.ExternalUnknown);

    private sealed class Session : IRazerCommandSession
    {
        internal byte[] Adapter { get; set; } = [0x11, 0x11];
        internal byte Mode { get; set; }
        internal byte Fan { get; set; }
        internal byte PowerFlags { get; set; }
        internal Action? AfterSet { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(item => item.CommandId < 0x80);
        internal List<FirmwareQueryOutcome> RetainedFailures { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public void RetainQueryFailure(byte classId, byte commandId, FirmwareQueryOutcome outcome) =>
            RetainedFailures.Add(outcome);

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            Assert.Null(assignTransactionId().Failure);
            byte[] payload = command.Arguments.ToArray();
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            switch ((command.ClassId, command.CommandId))
            {
                case (0x07, 0x8C): payload = Adapter; break;
                case (0x0D, 0x82): payload = [0, payload[1], Mode, Fan]; break;
                case (0x0D, 0x87): payload = [0, payload[1], 0]; break;
                case (0x0D, 0x81): payload = [0, payload[1], 25]; break;
                case (0x07, 0x8F): payload = [PowerFlags]; break;
                case (0x0D, 0x02): Mode = payload[2]; Fan = payload[3]; break;
                case (0x07, 0x0F): PowerFlags = payload[0]; break;
                default: throw new InvalidOperationException($"Unexpected command {command.ClassId:X2}/{command.CommandId:X2}.");
            }
            if (command.CommandId < 0x80)
            {
                AfterSet?.Invoke();
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

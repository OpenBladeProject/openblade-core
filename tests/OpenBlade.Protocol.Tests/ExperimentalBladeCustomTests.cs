using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeCustomTests
{
    private static readonly ExperimentalBladeModel Model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
    private static readonly CustomPerformanceSettings Requested = new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High);

    [Fact]
    public void Capabilities_ContainOnlyExistingRepresentableCombinations()
    {
        CustomPerformanceCapabilities capabilities = ExperimentalBladePort.CreateCustomCapabilities();
        Assert.True(capabilities.IsWellFormed);
        Assert.Equal(9, capabilities.AllowedCombinations.Count);
        Assert.Contains(new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High), capabilities.AllowedCombinations);
        Assert.DoesNotContain(new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.Medium), capabilities.AllowedCombinations);
        Assert.DoesNotContain(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), capabilities.AllowedCombinations);
    }

    [Fact]
    public async Task AdmittedModels_RejectUnadmittedGpuOffsetsWithoutAnyTraffic()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All.Where(item => item.ProductId != 0x02A0))
        {
            var session = new Session();
            var policy = new ExperimentalFeaturesPolicy(model);
            await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true, () => PowerSource.PluggedIn);
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
                Requested with { GpuClockOffsets = GpuClockOffsetSettings.Default }, CancellationToken.None));
            Assert.Empty(session.Commands);
        }
    }

    [Theory]
    [InlineData(false, PowerSource.PluggedIn)]
    [InlineData(true, PowerSource.Battery)]
    [InlineData(true, (PowerSource)99)]
    public async Task RecoveryAndPowerAdmission_PrecedeAllTraffic(bool enabled, PowerSource power)
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(Model);
        if (!enabled)
        {
            policy.RequireRecovery();
        }
        await using var port = Create(session, policy, () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 0)]
    public async Task ManualFansOrHyperBoost_RejectBeforeAnySetter(byte mode, byte fan)
    {
        var session = new Session { Mode = mode, Fan = fan };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        AssertNoWrites(session);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(255, 1)]
    [InlineData(2, 2)]
    public async Task UnrepresentableBaselineLevels_BlockBeforeEnteringCustom(byte cpu, byte gpu)
    {
        var session = new Session { Cpu = cpu, Gpu = gpu };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        AssertNoWrites(session);
        Assert.Contains(policy.Snapshot().Observations, item => item.Outcome == "UnsupportedState"
            && item.ConfirmedValue!.Contains($"CpuLevel={cpu}") && item.ConfirmedValue.Contains($"GpuLevel={gpu}"));
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(2, 2)]
    public async Task UnrepresentableCustomReadback_RejectsThermalStateAndRetainsRawDiagnostic(byte cpu, byte gpu)
    {
        var session = new Session { Mode = 4, Cpu = cpu, Gpu = gpu };
        var policy = Enabled();
        await using var port = Create(session, policy);
        RazerProtocolException failure = await Assert.ThrowsAsync<RazerProtocolException>(() =>
            port.QueryPerformanceFanStateAsync(CancellationToken.None));
        Assert.Equal(DeviceStateQuery.CustomPerformanceCombination, failure.StateQuery);
        Assert.Equal($"00 01 {cpu:X2} / 00 02 {gpu:X2}", failure.RejectedPayloadHex);
        Assert.True(DeviceStateContentRejection.IsContentRejection(failure));
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "CustomPerformance"
            && item.Outcome == "UnsupportedState"
            && item.ConfirmedValue == $"CpuSelector=1;CpuLevel={cpu};GpuSelector=2;GpuLevel={gpu}");
        AssertNoWrites(session);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BaselineQueryFailureOrWrongSelector_BlocksAllSetters(bool failRead, bool wrongSelector)
    {
        var session = new Session { FailLevelRead = failRead, WrongLevelSelector = wrongSelector };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        AssertNoWrites(session);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Setter_UsesBaselineThenModeConfirmationThenLevelsAndIndependentReadback()
    {
        var session = new Session();
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.SetCustomPerformanceAsync(Requested, CancellationToken.None);
        Assert.Equal(new byte[] { 0x82, 0x82, 0x87, 0x87, 0x02, 0x02, 0x82, 0x82, 0x87, 0x87, 0x07, 0x87, 0x87, 0x07, 0x87, 0x87, 0x82, 0x82, 0x87, 0x87 },
            session.Commands.Where(command => command.ClassId == 0x0D).Select(command => command.CommandId));
        Assert.Equal(new[] { "01010400", "01020400", "010101", "010202" },
            session.Commands.Where(command => command.CommandId < 0x80).Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Baseline" && item.ConfirmedValue == "Balanced/Automatic;CPU=Low;GPU=Low");
        Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Readback" && item.ConfirmedValue == "CPU=Medium;GPU=High");
        Assert.Equal(4, session.Commands.Count(command => command.ClassId == 0x07 && command.CommandId == 0x8C));
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0, 2, 2, 1, "010201", "010102")]
    [InlineData(2, 0, 1, 2, "010101", "010202")]
    public async Task SwitchingTheHighDomain_ConfirmsTheLoweringBeforeRaisingTheOther(
        byte cpu, byte gpu, byte targetCpu, byte targetGpu, string firstWrite, string secondWrite)
    {
        var session = new Session { Mode = 4, Cpu = cpu, Gpu = gpu };
        session.AfterCommand = command => Assert.False(session.Cpu == 2 && session.Gpu == 2);
        await using var port = Create(session, Enabled());
        var requested = new CustomPerformanceSettings((CustomPerformanceLevel)targetCpu, (CustomPerformanceLevel)targetGpu);
        await port.SetCustomPerformanceAsync(requested, CancellationToken.None);
        Assert.Equal(new[] { firstWrite, secondWrite }, session.Commands.Where(command => command.CommandId == 0x07)
            .Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.Equal(new byte[] { 0x82, 0x82, 0x87, 0x87, 0x07, 0x87, 0x87, 0x07, 0x87, 0x87, 0x82, 0x82, 0x87, 0x87 },
            session.Commands.Where(command => command.ClassId == 0x0D).Select(command => command.CommandId));
    }

    [Fact]
    public async Task Ordering_UsesLevelsConfirmedAfterEnteringCustom()
    {
        var session = new Session();
        session.AfterCommand = command =>
        {
            if (command.CommandId == 0x02 && command.Arguments.Span[1] == 2)
            {
                session.Gpu = 2;
            }
            Assert.False(session.Cpu == 2 && session.Gpu == 2);
        };
        await using var port = Create(session, Enabled());
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium), CancellationToken.None);
        Assert.Equal(new[] { "010201", "010102" }, session.Commands.Where(command => command.CommandId == 0x07)
            .Select(command => Convert.ToHexString(command.Arguments.Span)));
    }

    [Fact]
    public async Task UnchangedCluster_IsNotWrittenAgain()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 0 };
        await using var port = Create(session, Enabled());
        await port.SetCustomPerformanceAsync(Requested, CancellationToken.None);
        RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.Equal("010202", Convert.ToHexString(setter.Arguments.Span));
    }

    [Fact]
    public async Task UnexpectedOtherClusterChange_StopsBeforeTheNextSetter()
    {
        var session = new Session { Mode = 4 };
        var policy = Enabled();
        session.AfterCommand = command =>
        {
            if (command.CommandId == 0x07)
            {
                session.Gpu = 1;
            }
        };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task AlreadyCustomAndMatching_OnlyReadsBaseline()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2 };
        await using var port = Create(session, Enabled());
        await port.SetCustomPerformanceAsync(Requested, CancellationToken.None);
        Assert.Equal(new byte[] { 0x82, 0x82, 0x87, 0x87, 0x8F }, session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task ModeEchoWithoutChange_DoesNotSendLevelSettersAndPausesWrites()
    {
        var session = new Session { IgnoreModeWrites = true };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task LevelEchoWithoutChange_BlocksTheNextSetterWithoutReplay()
    {
        var session = new Session { IgnoreLevelWrites = true };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task FailedCpuSubmission_PausesAllExperimentalWritesAcrossPortsWithoutGpuWrite()
    {
        var session = new Session { FailCpuWrite = true };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
        var nextSession = new Session();
        await using var nextPort = Create(nextSession, policy);
        await Assert.ThrowsAsync<BladeWriteException>(() => nextPort.SetProtectionOffAsync(CancellationToken.None));
        Assert.Empty(nextSession.Commands);
    }

    [Fact]
    public async Task RecoveryRequiredAtCpuSubmission_PreventsLevelWritesAfterModeChange()
    {
        var session = new Session();
        var policy = Enabled();
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x07)
            {
                policy.RequireRecovery();
            }
        };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task CancellationAfterCpuWrite_StopsWithoutGpuWriteOrReplay()
    {
        var session = new Session();
        var policy = Enabled();
        using var cancellation = new CancellationTokenSource();
        session.AfterCommand = command =>
        {
            if (command.CommandId == 0x07)
            {
                cancellation.Cancel();
            }
        };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(Requested, cancellation.Token));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public void ObserverRequests_MatchTheProductionReportEncoderWithoutInventingReplies()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Blade18ObserverRequests.json")));
        Assert.False(fixture.RootElement.GetProperty("claimsPhysicalValidation").GetBoolean());
        Assert.Equal("02A0", fixture.RootElement.GetProperty("productId").GetString());
        JsonElement requests = fixture.RootElement.GetProperty("retainedRequests");
        Assert.Equal(12, requests.GetArrayLength());
        foreach (JsonElement request in requests.EnumerateArray())
        {
            byte[] report = Convert.FromHexString(request.GetProperty("reportHex").GetString()!);
            Assert.Equal(91, report.Length);
            Assert.Equal(0, report[0]);
            byte[] encoded = RazerReport.Create(report[2], report[7], report[8],
                report.AsSpan(9, report[6])).Encode();
            Assert.Equal(report[1..], encoded);
        }
    }

    [Fact]
    public async Task ObserverBoostHigh_ReadsChangesAndRestoresWithoutResettingThePreset()
    {
        var session = new Session { Mode = 4, Cpu = 3, Gpu = 2 };
        var policy = Enabled();
        await using var port = Create(session, policy);
        var baseline = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);
        Assert.Equal(baseline, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low), CancellationToken.None);
        int beforeRestore = session.Commands.Count;
        await port.SetCustomPerformanceAsync(baseline, CancellationToken.None);
        Assert.Equal(baseline, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
        Assert.Equal(new[] { "010202", "010103" }, session.Commands.Skip(beforeRestore)
            .Where(command => command.CommandId == 0x07)
            .Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x02);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(2, 0, 3, 2, "010101,010202,010103")]
    [InlineData(2, 1, 3, 2, "010101,010202,010103")]
    [InlineData(3, 2, 2, 0, "010101,010200,010102")]
    [InlineData(3, 2, 2, 1, "010101,010201,010102")]
    public async Task BoostAndHighCpu_UseTheShortestAdmittedBridge(
        byte cpu, byte gpu, byte targetCpu, byte targetGpu, string expectedWrites)
    {
        var session = new Session { Mode = 4, Cpu = cpu, Gpu = gpu };
        var policy = Enabled();
        session.AfterCommand = _ => Assert.Contains(
            new CustomPerformanceCombination((CustomPerformanceLevel)session.Cpu, (CustomPerformanceLevel)session.Gpu),
            ExperimentalBladePort.CreateCustomCapabilities(Model).AllowedCombinations);
        await using var port = Create(session, policy);
        var target = new CustomPerformanceSettings((CustomPerformanceLevel)targetCpu, (CustomPerformanceLevel)targetGpu);
        await port.SetCustomPerformanceAsync(target, CancellationToken.None);
        Assert.Equal(target, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
        Assert.Equal(expectedWrites.Split(','), session.Commands.Where(command => command.CommandId == 0x07)
            .Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x02);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(CustomPerformanceLevel.Boost, CustomPerformanceLevel.Low)]
    [InlineData(CustomPerformanceLevel.Boost, CustomPerformanceLevel.Medium)]
    [InlineData(CustomPerformanceLevel.High, CustomPerformanceLevel.High)]
    public async Task UnobservedBlade18Combinations_RejectBeforeAnyTraffic(
        CustomPerformanceLevel cpu, CustomPerformanceLevel gpu)
    {
        var session = new Session();
        await using var port = Create(session, Enabled());
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(cpu, gpu), CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task BridgeReadbackFailure_StopsBeforeBoostAndPausesWithoutReplay()
    {
        var session = new Session { Mode = 4, Cpu = 2, Gpu = 0 };
        var policy = Enabled();
        session.AfterCommand = command =>
        {
            if (command.CommandId == 0x07 && command.Arguments.Span[1] == 2)
            {
                session.Gpu = 0;
            }
        };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(new[] { "010101", "010202" }, session.Commands.Where(command => command.CommandId == 0x07)
            .Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task BoostReadbackMismatch_PausesBeforeTheNextWriteWithoutReplay()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 0, IgnoreLevelWrites = true };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId == 0x07);
        Assert.Equal("010202", Convert.ToHexString(setter.Arguments.Span));
        Assert.False(policy.WritesAllowed);
    }

    private static ExperimentalFeaturesPolicy Enabled() => new(Model);

    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy policy, Func<PowerSource>? power = null) =>
        new(session, new(), Model, policy, () => true, power ?? (() => PowerSource.PluggedIn));

    private static void AssertNoWrites(Session session) => Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));

    private sealed class Session : IRazerCommandSession
    {
        internal byte Mode { get; set; }
        internal byte Fan { get; set; }
        internal byte Cpu { get; set; }
        internal byte Gpu { get; set; }
        internal bool FailLevelRead { get; init; }
        internal bool WrongLevelSelector { get; init; }
        internal bool IgnoreModeWrites { get; init; }
        internal bool IgnoreLevelWrites { get; init; }
        internal bool FailCpuWrite { get; init; }
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal Action<RazerCommandSpec>? AfterCommand { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            Commands.Add(command);
            _ = assignTransactionId();
            if (command.ClassId == 7 && command.CommandId == 0x8C)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { 0x11, 0x11 }, null));
            }
            if (command.ClassId == 7 && command.CommandId == 0x8F)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { 0 }, null));
            }
            Assert.Equal(0x0D, command.ClassId);
            byte[] payload = command.Arguments.ToArray();
            switch (command.CommandId)
            {
                case 0x82:
                    payload = [0, payload[1], Mode, Fan];
                    break;
                case 0x87:
                    if (FailLevelRead)
                    {
                        return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
                    }
                    payload = [0, WrongLevelSelector ? (byte)9 : payload[1], payload[1] == 1 ? Cpu : Gpu];
                    break;
                case 0x02:
                    if (!IgnoreModeWrites)
                    {
                        Mode = payload[2];
                        Fan = payload[3];
                    }
                    break;
                case 0x07:
                    if (FailCpuWrite && payload[1] == 1)
                    {
                        return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)
                        {
                            Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.Timeout, null, null),
                        });
                    }
                    if (!IgnoreLevelWrites)
                    {
                        if (payload[1] == 1)
                        {
                            Cpu = payload[2];
                        }
                        else
                        {
                            Gpu = payload[2];
                        }
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected command {command.CommandId:X2}.");
            }
            AfterCommand?.Invoke(command);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

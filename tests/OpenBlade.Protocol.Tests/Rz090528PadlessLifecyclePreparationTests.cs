using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessLifecyclePreparationTests
{
    [Fact]
    public void Exact0528PreparationNeverBorrows2026Readiness()
    {
        var selected = PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity("RZ09-0528", "RZ09-05289EN4", "2.02"));
        Assert.NotNull(selected);
        Assert.Equal(PerformancePreset.Performance, selected.BaseMode);
        Assert.Equal(Rz090528PadlessLifecyclePreparation.ManifestSha256, selected.ManifestSha256);
        Assert.NotEqual(HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256, selected.ManifestSha256);
        // Every non-lifecycle gate is bound to merged 2025 evidence; lifecycle gates and production stay closed.
        Assert.True(selected.Readiness.LifecycleValidationReady);
        Assert.False(selected.Readiness.WriteAdmitted);
        Assert.Equal(new HyperBoostWithoutCoolingPadEvidenceGates(true, true, true, true, true, true, true, true, false, false), selected.Readiness);
        BatteryValidationFactory.RequireLifecycleReadiness(selected);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static byte[] Descriptor() => File.ReadAllBytes(Path.Combine(RepositoryRoot(),
        "src", "OpenBlade.Protocol", "Fixtures", "Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json"));

    [Fact]
    public void EveryCitedArtifactMatchesTheRepositoryBytes()
    {
        byte[] descriptor = Descriptor();
        Assert.Equal(Rz090528PadlessLifecyclePreparation.ManifestSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(descriptor)));
        using var document = System.Text.Json.JsonDocument.Parse(descriptor);
        int cited = 0;
        foreach (var gate in document.RootElement.GetProperty("gates").EnumerateObject())
        {
            foreach (var property in gate.Value.EnumerateObject().Where(p => p.Name is "evidence" or "supplementalEvidence"))
            {
                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Null) { continue; }
                string artifact = property.Value.GetProperty("artifact").GetString()!;
                string path = Path.Combine(RepositoryRoot(), artifact.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), artifact);
                Assert.Equal(property.Value.GetProperty("sha256").GetString(),
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
                cited++;
            }
        }
        Assert.Equal(9, cited);
    }

    [Theory]
    [InlineData("lowPowerLifecycleValidated")]
    [InlineData("crashRecoveryValidated")]
    public void LifecycleGatesCanNeverBeClaimedByReadinessEvidence(string gate)
    {
        string json = System.Text.Encoding.UTF8.GetString(Descriptor());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["gates"]![gate] = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"validated\":true,\"evidence\":{\"artifact\":\"captures/annotations/2026-09-29-rz09-0528-x.json\",\"sha256\":\"" + new string('A', 64) + "\"}}");
        Assert.Throws<UnvalidatedCommandException>(() => Rz090528PadlessLifecyclePreparation.ValidateContent(
            System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData("captures/annotations/2026-08-19-rz09-0581-hyperboost-padless-immediate.json")]
    [InlineData("captures/annotations/../annotations/2026-09-29-rz09-0528-x.json")]
    [InlineData("src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_4_00_PadAbsentHyperBoostNoLoadPlan.json")]
    [InlineData("C:/elsewhere/rz09-0528.json")]
    public void GatesCannotCiteAnotherModelOrPath(string artifact)
    {
        string json = System.Text.Encoding.UTF8.GetString(Descriptor());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["gates"]!["reviewedLoadValidated"]!["evidence"]!["artifact"] = artifact;
        Assert.Throws<UnvalidatedCommandException>(() => Rz090528PadlessLifecyclePreparation.ValidateContent(
            System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void GatesMustCarryAFullHash()
    {
        string json = System.Text.Encoding.UTF8.GetString(Descriptor());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["gates"]!["reviewedLoadValidated"]!["evidence"]!["sha256"] = "ABC";
        Assert.Throws<UnvalidatedCommandException>(() => Rz090528PadlessLifecyclePreparation.ValidateContent(
            System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData("RZ09-0528", "RZ09-05289EN4", "2.03")]
    [InlineData("RZ09-0528", "other", "2.02")]
    [InlineData("unknown", "RZ09-05289EN4", "2.02")]
    public void WrongExactTargetHasNoManifestOrValidationAuthority(string model, string sku, string bios)
    {
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity(model, sku, bios)));
        Assert.Throws<UnvalidatedCommandException>(() => BatteryValidationFactory.RequireLifecycleReadiness(null));
    }

    [Fact]
    public void IdentityFailureAndUnknownCompatibilityHaveNoFallback()
    {
        var identity = Identity("RZ09-0528", "RZ09-05289EN4", "2.02");
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(identity with { Failure = HidSessionFailureKind.IdentityBlocked }));
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(identity with { FirmwareCompatibility = HostFirmwareCompatibility.Unknown }));
    }

    [Fact]
    public void Existing2026SelectionRetainsBalancedManifestAndReadiness()
    {
        var selected = PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity("RZ09-0581", "RZ09-05819EN4", "4.00"));
        Assert.NotNull(selected);
        Assert.Equal(PerformancePreset.Balanced, selected.BaseMode);
        Assert.Equal(HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256, selected.ManifestSha256);
        Assert.Equal(HyperBoostWithoutCoolingPadEvidenceGates.Current, selected.Readiness);
    }

    [Fact]
    public void PreparationHashRejectsEvenPlausibleDescriptorChanges()
    {
        Assert.Throws<UnvalidatedCommandException>(() => Rz090528PadlessLifecyclePreparation.Validate("{}"u8.ToArray()));
    }

    [Fact]
    public async Task SyntheticDeveloperCompositionUsesOnlyPerformancePairAndAllowsSafeRestoreAfterPadArrival()
    {
        var eligibility = new Pad();
        var session = new Session();
        await using var port = Create(session, eligibility);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, port.Capabilities.HyperBoostWithoutCoolingPadAccess);
        await ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default);
        eligibility.State = CoolingPadConnectionState.PresentExact;
        await port.SetHyperBoostAsync(false, PerformancePreset.Performance, default);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.Equal(2, session.PowerQueries);
        int count = session.Commands;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(true, PerformancePreset.Balanced, default));
        Assert.Equal(count, session.Commands);
    }

    [Fact]
    public async Task NormalEnableCannotUseDeveloperPadlessAuthority()
    {
        var session = new Session();
        await using var port = Create(session, new Pad());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    public async Task DeveloperEntryRequiresExactAbsence(CoolingPadConnectionState state)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = state });
        await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task WrongBaselineOrInsufficientPowerCannotSubmit()
    {
        foreach (bool wrongBaseline in new[] { true, false })
        {
            var session = new Session { InitialMode = wrongBaseline ? (byte)0 : (byte)2, FullPower = wrongBaseline };
            await using var port = Create(session, new Pad());
            await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
            Assert.Empty(session.Writes);
        }
    }

    [Fact]
    public async Task PadArrivalBetweenChannelsPoisonsWithoutReplay()
    {
        var pad = new Pad();
        var session = new Session { AfterFirstWrite = () => pad.State = CoolingPadConnectionState.PresentExact };
        await using var port = Create(session, pad);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        int count = session.Commands;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(false, PerformancePreset.Performance, default));
        Assert.Equal(count, session.Commands);
    }

    [Fact]
    public async Task NativeSubmissionGateRequiresAbsenceAtSubmission()
    {
        var pad = new Pad();
        var session = new Session { BeforeWrite = () => pad.State = CoolingPadConnectionState.ObservationFailed };
        await using var port = Create(session, pad);
        await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Empty(session.Writes);
        Assert.True(session.NativeGateRefused);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent)]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    public async Task AdmittedProductionUsesThePerformancePairOnEitherPadPath(CoolingPadConnectionState state)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = state }, padlessAdmitted: true);

        await port.SetHyperBoostAsync(true, PerformancePreset.Performance, default);
        await port.SetHyperBoostAsync(false, PerformancePreset.Performance, default);

        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent)]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    public async Task AdmittedProductionEntersFromCustomAndRestoresTheCustomPair(CoolingPadConnectionState state)
    {
        var session = new Session { InitialMode = 4 };
        await using var port = Create(session, new Pad { State = state }, padlessAdmitted: true,
            baseModes: new(DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite)
            {
                Custom = DeviceFeatureAccess.ReadWrite,
            });

        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, default);
        await port.SetHyperBoostAsync(false, PerformancePreset.Custom, default);

        Assert.Equal(new[] { "01010700", "01020700", "01010400", "01020400" }, session.Writes);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent, CoolingPadConnectionState.PresentExact)]
    [InlineData(CoolingPadConnectionState.PresentExact, CoolingPadConnectionState.Absent)]
    public async Task AdmittedProductionLocksTheEntryPadPathForEveryCommand(
        CoolingPadConnectionState entry,
        CoolingPadConnectionState changed)
    {
        var pad = new Pad { State = entry };
        var session = new Session { AfterFirstWrite = () => pad.State = changed };
        await using var port = Create(session, pad, padlessAdmitted: true);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
    }

    [Theory]
    [InlineData(true, CoolingPadConnectionState.Absent)]
    [InlineData(false, CoolingPadConnectionState.PresentExact)]
    public async Task AdmittedProductionSubmitsOnlyUnderTheJournaledPadPath(
        bool requiresCoolingPad,
        CoolingPadConnectionState observed)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = observed }, padlessAdmitted: true);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(true, PerformancePreset.Performance, requiresCoolingPad, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(true, CoolingPadConnectionState.PresentExact)]
    [InlineData(false, CoolingPadConnectionState.Absent)]
    public async Task AdmittedProductionAppliesUnderTheMatchingJournaledPadPath(
        bool requiresCoolingPad,
        CoolingPadConnectionState observed)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = observed }, padlessAdmitted: true);

        await port.SetHyperBoostAsync(true, PerformancePreset.Performance, requiresCoolingPad, default);

        Assert.Equal(new[] { "01010700", "01020700" }, session.Writes);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    public async Task AdmittedProductionRejectsAnUnconfirmedPadState(CoolingPadConnectionState state)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = state }, padlessAdmitted: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task AdmittedProductionNeverComposesAnUncapturedBalancedEntry()
    {
        var session = new Session();
        await using var port = Create(session, new Pad(), padlessAdmitted: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => port.SetHyperBoostAsync(true, PerformancePreset.Balanced, default));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task AdmittedProductionRequiresTheFullPowerAdapter()
    {
        var session = new Session { FullPower = false };
        await using var port = Create(session, new Pad(), padlessAdmitted: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Empty(session.Writes);
    }

    private static HostIdentityReadResult Identity(string model, string sku, string bios) =>
        new(new("Razer", "Blade 16 - " + model, sku, bios), null, HostFirmwareCompatibility.Tested);

    [Fact]
    public void PadlessWriteAccessRequiresAWritableDirectBaseMode()
    {
        Assert.Throws<UnvalidatedCommandException>(() => Create(
            new Session(),
            new Pad(),
            padlessAdmitted: true,
            baseModes: new(DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly)));
    }

    private static ProductionBatteryPort Create(
        Session session,
        Pad pad,
        bool padlessAdmitted = false,
        HyperBoostBaseModeCapabilities? baseModes = null)
    {
        var target = new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02");
        CapturedCommandFixture Fixture(string action, byte channel, byte mode) => new(target, action, 0x0D, 0x02,
            [1, channel, mode, 0], true, true, true, true, true);
        var performance = DeviceCommandCatalog.Create(target,
            [Fixture(ProductionBatteryPort.PerformanceBoostFirstAction, 1, 2), Fixture(ProductionBatteryPort.PerformanceBoostSecondAction, 2, 2),
             Fixture(ProductionBatteryPort.PerformanceCustomFirstAction, 1, 4), Fixture(ProductionBatteryPort.PerformanceCustomSecondAction, 2, 4)]);
        var hyperBoost = DeviceCommandCatalog.Create(target,
            [Fixture(ProductionBatteryPort.HyperBoostEnableFirstAction, 1, 7), Fixture(ProductionBatteryPort.HyperBoostEnableSecondAction, 2, 7)]);
        return new(session, performance, BatteryValidationFactory.CreateRz090528ProductionBatteryReadEvidence(),
            new ValidationRunContext(), TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates: new HashSet<AdapterPowerState> { AdapterPowerState.FullPower280W },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: padlessAdmitted ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly,
            hyperBoostBaseModes: baseModes
                ?? new(DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite),
            hyperBoostCatalog: hyperBoost, hyperBoostEligibility: pad, allowPadlessHyperBoostLifecycleValidation: !padlessAdmitted,
            padlessHyperBoostLifecycleBaseMode: PerformancePreset.Performance);
    }

    private sealed class Pad : IHyperBoostEligibilitySource
    {
        internal CoolingPadConnectionState State = CoolingPadConnectionState.Absent;
        public CoolingPadConnectionState ObserveCoolingPad() => State;
    }

    private sealed class Session : IRazerCommandSession
    {
        internal byte InitialMode = 2;
        internal bool FullPower = true;
        internal Action? AfterFirstWrite;
        internal Action? BeforeWrite;
        internal readonly List<string> Writes = [];
        internal int Commands, PowerQueries;
        internal bool NativeGateRefused;
        private readonly Dictionary<byte, byte> modes = [];
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = assignTransactionId(); Commands++;
            byte[] input = command.Arguments.ToArray();
            byte[] response;
            if (command.CommandId == 0x8C) { PowerQueries++; response = FullPower ? [0x11, 0x11] : [0x07, 0x11]; }
            else if (command.CommandId == 0x82) { response = [1, input[1], modes.GetValueOrDefault(input[1], InitialMode), 0]; }
            else if (command.CommandId == 0x87) { response = [1, input[1], 1]; }
            else if (command.CommandId == 2)
            {
                BeforeWrite?.Invoke();
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    NativeGateRefused = true;
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked));
                }
                modes[input[1]] = input[2]; Writes.Add(Convert.ToHexString(input)); response = input;
                if (Writes.Count == 1) { AfterFirstWrite?.Invoke(); }
            }
            else { throw new InvalidOperationException("Unexpected test command"); }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class CpuVoltageReadOnlyCleanupTests
{
    [Fact]
    public void PriorOwnerOutputMustFinishBeforeCleanupCanRearm()
    {
        WindowsCpuVoltageReadOnlyCleanup.RequireOutputTerminal(Task.CompletedTask, CancellationToken.None);
        Assert.Throws<AggregateException>(() => WindowsCpuVoltageReadOnlyCleanup.RequireOutputTerminal(
            Task.FromException(new IOException("reader failed")), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var pending = new TaskCompletionSource();
        Assert.Throws<OperationCanceledException>(() => WindowsCpuVoltageReadOnlyCleanup.RequireOutputTerminal(pending.Task, cancelled.Token));
        Assert.False(pending.Task.IsCompleted);
    }
    [Fact]
    public void KnownCpuRecoveryBlocksResourceShutdownWithoutRequiringOrRefreshingCpuState()
    {
        WindowsCpuVoltageServiceProcess.RequireNoCpuRecovery(null);
        var backend = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        var cpu = new CpuTuningStatus(null, null, backend, backend, CpuTuningPendingBoot.Unknown, false);
        WindowsCpuVoltageServiceProcess.RequireNoCpuRecovery(cpu);
        Assert.Throws<IOException>(() => WindowsCpuVoltageServiceProcess.RequireNoCpuRecovery(cpu with { RestorePending = true }));
        Assert.Throws<IOException>(() => WindowsCpuVoltageServiceProcess.RequireNoCpuRecovery(cpu with
        { LiveRecovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer) }));
        Assert.Throws<IOException>(() => WindowsCpuVoltageServiceProcess.RequireNoCpuRecovery(cpu with
        { FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer) }));
    }
    [Fact]
    public void FailedDurableCleanupAcceptanceCannotBeRetriedOrLaunchEffects()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        state.Complete(false, false, null, "parked");
        Assert.Throws<IOException>(() => state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => throw new IOException("disk")));
        Assert.True(state.MustRetainHost);
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => throw new Exception("retry")));
    }
    [Fact]
    public void ExplicitCleanupIsDurableNeverReplayedAndAllowsOnlyThreeFreshObservations()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        for (int i = 0; i < 3; i++)
        {
            var observation = Request(CpuVoltageSessionAction.ObserveOnly);
            Assert.True(state.Accept(observation, _ => { }));
            state.Complete(false, false, null, "parked");
            var cleanup = Request(CpuVoltageSessionAction.CleanupReadOnly);
            bool persisted = false;
            Assert.True(state.Accept(cleanup, _ => { persisted = true; Assert.Equal("Blocked", state.Snapshot.State); }));
            Assert.True(persisted); Assert.Equal("CleaningReadOnly", state.Snapshot.State);
            Assert.False(state.Accept(cleanup, _ => throw new Exception("replay")));
            Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("concurrent")));
            state.CompleteCleanup(true, null, "cleaned");
            Assert.False(state.MustRetainHost); Assert.False(state.Snapshot.ObservationAdmitted);
            Assert.False(state.Accept(observation, _ => throw new Exception("old observation replay")));
            Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("unearned cycle")));
        }
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("fourth")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertaintyOrStickySessionBlockCannotBeClearedByCleanup(bool cleanupSucceeds)
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        state.Complete(false, false, null, "parked");
        state.Block("lock/power");
        Assert.True(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => { }));
        state.CompleteCleanup(cleanupSucceeds, null, "result");
        Assert.Equal("Blocked", state.Snapshot.State);
        Assert.Equal(!cleanupSucceeds, state.MustRetainHost);
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("admission revived")));
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => throw new Exception("cleanup retried")));
    }
    [Fact]
    public void CleanupCannotTargetRunningObservationOrAnyVoltageCycle()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => { }));
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => { }));
        state.Complete(true, true, null, "admitted");
        state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => { });
        state.Complete(false, false, null, "uncertain cycle");
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.CleanupReadOnly), _ => throw new Exception("cycle cleanup")));
    }
    [Fact]
    public void CleanupUsesExistingAuthenticationAndCanonicalFixedProtocol()
    {
        var request = Request(CpuVoltageSessionAction.CleanupReadOnly);
        Assert.Equal(request, CpuVoltageSessionProtocol.Parse(JsonSerializer.SerializeToUtf8Bytes(request, CpuVoltageSessionProtocol.Json)));
        Assert.Throws<ArgumentException>(() => WindowsCpuVoltageSessionPlatform.OwnerArguments(
            new(@"C:\fixed", new string('a', 40), new string('B', 64), "1.2.3+x"), request.Action));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void CleanupStopsAtEveryFailedProofOrEffectWithoutRetryOrRelease(int failure)
    {
        var platform = new OrderedPlatform(failure);
        string[] order = ["validate", "service terminal", "validate", "owner terminal", "validate", "driver absent", "validate", "receipt and release"];
        if (failure is >= 0 and < 8) { Assert.Throws<IOException>(() => CpuVoltageReadOnlyCleanup.Execute(platform)); }
        else { CpuVoltageReadOnlyCleanup.Execute(platform); }
        Assert.Equal(order.Take(failure is >= 0 and < 8 ? failure + 1 : 8), platform.Calls);
    }
    [Theory]
    [InlineData("CpuVoltageOwner=Unknown_IOException", true)]
    [InlineData("CpuVoltageOwner=CompletionOrCleanupUnknown", true)]
    [InlineData("CpuVoltageOwner=ResourcesRetained_NoRetry_NoCleanup", false)]
    [InlineData("CpuVoltageOwner=ObservationRecorded_ServiceTerminal_OwnedDriverAbsent", false)]
    [InlineData("CpuVoltageOwner=Unknown_bad\nvalue", false)]
    [InlineData(null, false)]
    public void OnlyActualFinalParkReasonsPermitRunningOwnerCleanup(string? line, bool expected) =>
        Assert.Equal(expected, CpuVoltageReadOnlyCleanup.IsParkReason(line));

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public void ActualTypedReadonlyResponsePlusExactLifecycleProvesOnlyResourceCleanup(ControlOperationStatus status)
    {
        var fixture = new Fixture(status);
        var proof = fixture.Verify();
        Assert.Equal(fixture.Run, proof.Tag.Run); Assert.Equal(fixture.Child, proof.Child);
        Assert.Equal(64, proof.ResultSha256.Length); Assert.Equal(64, proof.JournalSha256.Length);
    }
    [Theory]
    [InlineData("apply")]
    [InlineData("restore")]
    [InlineData("cycle")]
    [InlineData("context")]
    [InlineData("service")]
    [InlineData("child")]
    [InlineData("journal-tail")]
    [InlineData("journal-tag")]
    [InlineData("missing-response")]
    [InlineData("restoration")]
    public void MissingDriftedOrVoltageEvidenceNeverAdmitsCleanup(string corruption)
    {
        var fixture = new Fixture(ControlOperationStatus.Blocked);
        if (corruption.StartsWith("journal", StringComparison.Ordinal))
        {
            string journal = fixture.Records["control/lifecycle.jsonl"].GetString()!;
            journal = corruption == "journal-tail" ? journal.TrimEnd('\n') : journal.Replace("ChildBound", "StopIntent", StringComparison.Ordinal);
            fixture.Records["control/lifecycle.jsonl"] = JsonSerializer.SerializeToElement(journal);
        }
        else if (corruption == "restoration") { fixture.Records["control/voltage-restoration-verified.json"] = JsonSerializer.SerializeToElement(new { }); }
        else
        {
            string key = corruption is "context" or "service" ? "control/voltage-context.json"
                : corruption == "child" ? "control/voltage-child.json" : "evidence/cpu-voltage-optimizer.json";
            var value = JsonNode.Parse(fixture.Records[key].GetRawText())!;
            if (corruption == "service") { value["Identity"]!["Created"] = 1; }
            else if (corruption == "child") { value["ExecutableSha256"] = new string('C', 64); }
            else if (corruption == "context") { value["Mode"] = "Cycle"; }
            else if (corruption == "cycle") { value["mode"] = "Cycle"; }
            else if (corruption == "missing-response") { value.AsObject().Remove("controlResponse"); }
            else { value[corruption == "apply" ? "applyAttempted" : "restoreAttempted"] = true; }
            fixture.Records[key] = JsonSerializer.SerializeToElement(value);
        }
        Assert.ThrowsAny<Exception>(() => fixture.Verify());
    }
    [Fact]
    public void SharedJournalReadRequiresBoundedCompleteStableBytes()
    {
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("record\n"));
        Assert.Equal("record\n", Encoding.UTF8.GetString(WindowsCpuVoltageSessionPlatform.ReadCoherentJournal(valid)));
        using var partial = new MemoryStream(Encoding.UTF8.GetBytes("record"));
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.ReadCoherentJournal(partial));
        using var oversized = new MemoryStream(new byte[CpuPmDurableDriverJournal.MaximumBytes + 1]);
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.ReadCoherentJournal(oversized));
        using var changed = new ChangingStream();
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.ReadCoherentJournal(changed));
    }
    private sealed class ChangingStream() : MemoryStream(Encoding.UTF8.GetBytes("record\n"))
    {
        private int reads;
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            if (++reads == 2 && count > 0) { buffer[0] ^= 1; }
            return count;
        }
    }
    private sealed class OrderedPlatform(int failure) : ICpuVoltageReadOnlyCleanupPlatform
    {
        internal List<string> Calls = [];
        private void Step(string value) { Calls.Add(value); if (Calls.Count - 1 == failure) { throw new IOException("uncertain"); } }
        public void Validate() => Step("validate");
        public void ShutdownServiceAndVerifyTerminal() => Step("service terminal");
        public void TerminateParkedOwnerAndVerifyTerminal() => Step("owner terminal");
        public void StopDeleteOwnedDriverAndVerifyAbsence() => Step("driver absent");
        public void RecordCompletionAndRelease() => Step("receipt and release");
    }
    private static CpuVoltageSessionRequest Request(CpuVoltageSessionAction action) => new(1, Guid.NewGuid(), action);
    private sealed class Fixture
    {
        internal readonly Guid Run = Guid.NewGuid();
        internal readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly CpuVoltageSessionEnrollment Enrollment = new(Guid.NewGuid(), "S-1-5-21-1-2-3-1000", 1, 123, 456,
            @"C:\fixed\OpenBlade.Capture.exe", new string('a', 40), new string('B', 64), "1.2.3+voltage-validation.aaaaaaaa");
        internal readonly CpuPmProcessObservation Service = new(99, 234, CpuPmChildState.Running, WindowsCpuVoltageServiceProcess.Image);
        internal readonly CpuPmAuthorityPeer Child;
        internal readonly Dictionary<string, JsonElement> Records = [];
        internal Fixture(ControlOperationStatus status)
        {
            Child = new(321, Now.UtcDateTime.ToFileTimeUtc(), @"C:\fixed\firmware-smoke\OpenBlade.FirmwareSmoke.exe", new string('D', 64));
            Records["evidence/cpu-voltage-optimizer.json"] = JsonSerializer.SerializeToElement(
                CpuVoltageObservationEvidence.Create(Enrollment.ServiceVersion, Now,
                    new ControlOperationResult(status, "actual response")), CpuVoltageSessionProtocol.Json);
            Records["control/voltage-context.json"] = JsonSerializer.SerializeToElement(new
            {
                run = Run,
                Enrollment.Source,
                Manifest = Enrollment.Manifest,
                ServiceVersion = Enrollment.ServiceVersion,
                Identity = Service,
                Mode = "ObserveOnly",
                BudgetSeconds = 600,
            });
            Records["control/voltage-child.json"] = JsonSerializer.SerializeToElement(Child);
            var tag = new CpuPmDriverOwnerTag(Run, Guid.NewGuid(), Enrollment.Manifest, new string('a', 64));
            string[] phases = ["CreateIntent", "Created", "TagIntent", "Tagged", "StartIntent", "Started", "ChildLaunchIntent", "ChildBound"];
            string journal = string.Concat(phases.Select((phase, i) => JsonSerializer.Serialize(new
            {
                version = 1,
                sequence = i + 1,
                run = Run.ToString("N"),
                lifetime = tag.Lifetime.ToString("N"),
                owner = tag.Value,
                phase,
            }) + "\n"));
            Records["control/lifecycle.jsonl"] = JsonSerializer.SerializeToElement(journal);
        }
        internal CpuVoltageReadOnlyCleanupProof Verify() => CpuVoltageReadOnlyCleanup.Verify(Enrollment, Run, Service,
            Child.ExecutablePath, Child.ExecutableSha256, Records, Now.AddSeconds(-1), Now.AddSeconds(1));
    }
}

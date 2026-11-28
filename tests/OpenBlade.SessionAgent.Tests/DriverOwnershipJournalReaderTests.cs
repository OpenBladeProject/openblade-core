using System.Text.Json;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class DriverOwnershipJournalReaderTests
{
    private static readonly string PreviousBoot = DateTime.UtcNow.AddDays(-4).ToString("O");
    private static readonly string CurrentBoot = DateTime.UtcNow.AddDays(-1).ToString("O");

    [Fact]
    public void WindowsSnapAdmitsLegacyJournalOnlyAfterLaterBootAndLeavesEvidenceUnchanged()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            string json = JsonSerializer.Serialize(new { BootIdentity = PreviousBoot, BaselineHooks = Array.Empty<byte[]>() });
            File.WriteAllText(path, json);
            Assert.Equal(KeyboardDriverFeatures.SnapTap, DriverOwnershipJournalReader.Read(path)!.Features);
            Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => PreviousBoot));
            Assert.Null(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
            Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => "unknown"));
            Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(PreviousBoot, PreviousBoot, []));
            Assert.True(SnapTapDriverSupervisor.CanDiscardJournal(PreviousBoot, CurrentBoot, []));
            Assert.Equal(json, File.ReadAllText(path));
        });
    }

    [Fact]
    public void SameBootJournalReleasesOnlyOnExplicitRetryWithNoHooksAndReleasedInput()
    {
        Assert.True(SnapTapDriverSupervisor.CanRetrySameBootJournal(true, CurrentBoot, CurrentBoot, [], true));
        Assert.False(SnapTapDriverSupervisor.CanRetrySameBootJournal(false, CurrentBoot, CurrentBoot, [], true));
        Assert.False(SnapTapDriverSupervisor.CanRetrySameBootJournal(true, CurrentBoot, CurrentBoot, [], false));
        // A journal from a later or unknown boot is not this session's evidence.
        Assert.False(SnapTapDriverSupervisor.CanRetrySameBootJournal(true, CurrentBoot, PreviousBoot, [], true));
        Assert.False(SnapTapDriverSupervisor.CanRetrySameBootJournal(true, null, CurrentBoot, [], true));
        foreach (byte[] hook in new[]
        {
            RazerSnapTapDriverProtocol.EncodeHook(0x1E),
            RazerSnapTapDriverProtocol.EncodeHook(0x3B, RazerKeyboardHookPurpose.FunctionRow),
        })
        {
            Assert.False(SnapTapDriverSupervisor.CanRetrySameBootJournal(true, CurrentBoot, CurrentBoot, [hook], true));
        }
    }

    [Fact]
    public void RetryReleaseWaitIsBoundedAndStopsOnRequest()
    {
        int checks = 0;
        Assert.True(SnapTapDriverSupervisor.WaitForReleasedInput(() => ++checks >= 3, () => false, 5000));
        Assert.Equal(3, checks);
        Assert.False(SnapTapDriverSupervisor.WaitForReleasedInput(() => false, () => false, 120));
        Assert.False(SnapTapDriverSupervisor.WaitForReleasedInput(() => true, () => true, 5000));
    }

    [Fact]
    public void CombinedJournalUsesSameBootAdmissionForBothWindowsBackends()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            Write(path, KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow);
            byte[] original = File.ReadAllBytes(path);
            Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => PreviousBoot));
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => PreviousBoot));
            Assert.Null(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
            Assert.Null(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            Assert.Equal(original, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void WindowsSnapDoesNotReadDisjointFunctionRowJournal()
    {
        WithDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "function-row-driver-ownership.json"), "incomplete Fn-only evidence");
            Assert.Null(SnapTapInputHost.InspectWindowsDriverOwnership(directory,
                () => throw new InvalidOperationException("No A/D journal needs a boot read.")));
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
        });
    }

    [Fact]
    public void TransientReadFailureIsClosedAndAFreshReadCanObserveLaterBootWithoutDeletingJournal()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            Write(path, KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => DriverOwnershipJournalReader.Read(path));
                Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
                Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            }
            Assert.Null(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
            Assert.Null(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            Assert.True(File.Exists(path));
        });
    }

    [Theory]
    [InlineData(256, 292, true)]
    [InlineData(257, 292, false)]
    [InlineData(1, 291, false)]
    [InlineData(1, 293, false)]
    public void SharedReaderPreservesReviewedHookGeometryAndEnumerationBound(int count, int length, bool valid)
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            byte[][] hooks = Enumerable.Range(0, count).Select(_ => new byte[length]).ToArray();
            Write(path, KeyboardDriverFeatures.SnapTap, hooks);
            if (valid) { Assert.Equal(count, DriverOwnershipJournalReader.Read(path)!.BaselineHooks.Length); }
            else
            {
                Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
                Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
                Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            }
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void InvalidJournalNeverAuthorizesWindowsSelection(string json)
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            File.WriteAllText(path, json);
            Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => CurrentBoot));
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            Assert.Equal(json, File.ReadAllText(path));
        });
    }

    [Fact]
    public void OversizedInvalidScopeAndUnknownBootRemainClosed()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            File.WriteAllText(path, new string(' ', DriverOwnershipJournalReader.MaximumLength + 1));
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
            Write(path, KeyboardDriverFeatures.FunctionRow);
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
            Write(path, (KeyboardDriverFeatures)4);
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
            File.WriteAllText(path, JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal("unknown", [])));
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
        });
        Assert.False(DriverOwnershipJournalReader.IsLaterBoot(CurrentBoot, PreviousBoot));
        Assert.False(DriverOwnershipJournalReader.IsLaterBoot(PreviousBoot, PreviousBoot));
        Assert.False(DriverOwnershipJournalReader.IsLaterBoot(PreviousBoot, DateTime.UtcNow.AddDays(1).ToString("O")));
    }

    private const int OwnerId = 4242;
    private const int WorkerId = 4343;
    private static readonly DateTimeOffset JournalCreatedAt = DateTimeOffset.UtcNow.AddHours(-2);

    private static SnapTapDriverSupervisor.OwnershipJournal OwnedJournal(
        KeyboardDriverFeatures features = KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow) =>
        new(CurrentBoot, [], features, OwnerId, JournalCreatedAt);

    private static bool TryTerminated(SnapTapDriverSupervisor.OwnershipJournal journal, string evidencePath,
        out IReadOnlyList<byte[]>? hooks, Func<int, DateTimeOffset, bool>? isLiveSince = null, int currentId = 1) =>
        SnapTapDriverSupervisor.TryReadTerminatedOwner(journal, currentId, isLiveSince ?? ((_, _) => false),
            _ => evidencePath, out _, out hooks, out _);

    [Fact]
    public void TerminatedOwnerRequiresAnIdentifiedUnlatchedJournalWhoseSupervisorIsGone()
    {
        WithDirectory(directory =>
        {
            string missing = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            // An owner killed while idle leaves no evidence and no hooks to clear.
            Assert.True(TryTerminated(OwnedJournal(), missing, out var hooks));
            Assert.Empty(hooks!);
            // Journals from earlier builds cannot name their owner: explicit retry only.
            Assert.False(TryTerminated(OwnedJournal() with { SupervisorProcessId = null, CreatedAt = null }, missing, out _));
            // A supervisor that chose to latch keeps its journal for explicit recovery.
            Assert.False(TryTerminated(OwnedJournal() with { Latched = true }, missing, out _));
            Assert.False(TryTerminated(OwnedJournal(), missing, out _, currentId: OwnerId));
            Assert.False(TryTerminated(OwnedJournal(), missing, out _,
                (id, notAfter) => id == OwnerId && notAfter == JournalCreatedAt));
        });
    }

    [Fact]
    public void TerminatedOwnerHooksComeOnlyFromItsDeadWorkersFreshEvidence()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            byte[] snap = RazerSnapTapDriverProtocol.EncodeHook(0x1E, RazerKeyboardHookPurpose.Combined);
            byte[] fn = RazerSnapTapDriverProtocol.EncodeHook(0x3B, RazerKeyboardHookPurpose.Combined);
            DateTimeOffset updated = JournalCreatedAt.AddMinutes(5);
            WriteEvidence(path, WorkerId, updated, snap, fn);
            Assert.True(TryTerminated(OwnedJournal(), path, out var hooks));
            Assert.Equal(new[] { snap, fn }, hooks);

            // A live process that may be the worker blocks recovery; a reused id does not.
            Assert.False(TryTerminated(OwnedJournal(), path, out _,
                (id, notAfter) => id == WorkerId && notAfter == updated));

            // Evidence older than the journal belongs to an earlier owner with the same id.
            WriteEvidence(path, WorkerId, JournalCreatedAt.AddSeconds(-1), snap);
            Assert.False(TryTerminated(OwnedJournal(), path, out _));

            // Records outside the journal's scope, duplicates and malformed files refuse.
            WriteEvidence(path, WorkerId, updated, fn);
            Assert.False(TryTerminated(OwnedJournal(KeyboardDriverFeatures.SnapTap), path, out _));
            WriteEvidence(path, WorkerId, updated, snap, snap);
            Assert.False(TryTerminated(OwnedJournal(), path, out _));
            WriteEvidence(path, 0, updated, snap);
            Assert.False(TryTerminated(OwnedJournal(), path, out _));
            File.WriteAllText(path, "not json");
            Assert.False(TryTerminated(OwnedJournal(), path, out _));
        });
    }

    [Fact]
    public void ReconciliationRefusesAnOpenBladeHookTheTerminatedOwnerDidNotEvidence()
    {
        var transport = new ListTransport(RazerSnapTapDriverProtocol.EncodeHook(0x1E));
        Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => Assert.Fail(), out var failure,
            RazerKeyboardHookPurpose.SnapTap, confirmedOwned: []));
        Assert.Equal("HookOwnershipChanged", failure);
        Assert.Equal(0, transport.Clears);

        byte[] owned = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
        transport = new ListTransport(owned);
        Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => { }, out failure,
            RazerKeyboardHookPurpose.SnapTap, confirmedOwned: [owned]));
        Assert.Null(failure);
        Assert.Equal(1, transport.Clears);
        Assert.Empty(transport.Hooks);
    }

    [Fact]
    public void LatchMarkPreservesTheJournalAndBlocksTerminatedOwnerRecovery()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            byte[] baseline = new byte[RazerSnapTapDriverProtocol.HookRecordLength];
            File.WriteAllText(path, JsonSerializer.Serialize(OwnedJournal() with { BaselineHooks = [baseline] }));
            Assert.True(SnapTapDriverSupervisor.MarkJournalLatched(path, Path.Combine(directory, "evidence.json")));
            Assert.False(File.Exists(Path.Combine(directory, "evidence.json")));
            var marked = DriverOwnershipJournalReader.Read(path)!;
            Assert.True(marked.Latched);
            Assert.Equal(OwnerId, marked.SupervisorProcessId);
            Assert.Equal(JournalCreatedAt, marked.CreatedAt);
            Assert.Equal(CurrentBoot, marked.BootIdentity);
            Assert.Equal(baseline, Assert.Single(marked.BaselineHooks));
            Assert.False(TryTerminated(marked, Path.Combine(directory, "missing.json"), out _));
            Assert.False(File.Exists(path + ".tmp"));
            // Windows backends still treat a latched journal as same-boot ownership.
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
        });
    }

    [Fact]
    public void BlockedLatchMarkFailsClosedThroughARefusalMarker()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            string evidence = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            File.WriteAllText(path, JsonSerializer.Serialize(OwnedJournal()));
            WriteEvidence(evidence, WorkerId, JournalCreatedAt.AddMinutes(5),
                RazerSnapTapDriverProtocol.EncodeHook(0x1E, RazerKeyboardHookPurpose.Combined));
            // A reader holding the journal with the shared reader's own sharing mode
            // blocks the replace on every attempt.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(SnapTapDriverSupervisor.MarkJournalLatched(path, evidence));
            }
            Assert.False(DriverOwnershipJournalReader.Read(path)!.Latched);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(TryTerminated(OwnedJournal(), evidence, out _));
        });
    }

    [Fact]
    public void ClearedLatchRestoresTheIdentifiedJournal()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            byte[] baseline = new byte[RazerSnapTapDriverProtocol.HookRecordLength];
            File.WriteAllText(path, JsonSerializer.Serialize(OwnedJournal() with { BaselineHooks = [baseline], Latched = true }));
            Assert.True(SnapTapDriverSupervisor.ClearJournalLatch(path));
            var reopened = DriverOwnershipJournalReader.Read(path)!;
            Assert.False(reopened.Latched);
            Assert.Equal(OwnerId, reopened.SupervisorProcessId);
            Assert.Equal(JournalCreatedAt, reopened.CreatedAt);
            Assert.Equal(CurrentBoot, reopened.BootIdentity);
            Assert.Equal(baseline, Assert.Single(reopened.BaselineHooks));
            Assert.False(File.Exists(path + ".tmp"));
            // Clearing an already open journal changes nothing.
            Assert.True(SnapTapDriverSupervisor.ClearJournalLatch(path));
            Assert.False(DriverOwnershipJournalReader.Read(path)!.Latched);
        });
    }

    [Fact]
    public void ClearingALatchFailsClosed()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            // A missing journal cannot be reopened, so the caller must not rearm.
            Assert.False(SnapTapDriverSupervisor.ClearJournalLatch(path));
            Assert.False(File.Exists(path));
            File.WriteAllText(path, JsonSerializer.Serialize(OwnedJournal() with { Latched = true }));
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(SnapTapDriverSupervisor.ClearJournalLatch(path));
            }
            Assert.True(DriverOwnershipJournalReader.Read(path)!.Latched);
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void RefusalMarkerBlocksReuse()
    {
        WithDirectory(directory =>
        {
            string evidence = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            WriteEvidence(evidence, WorkerId, JournalCreatedAt.AddMinutes(5),
                RazerSnapTapDriverProtocol.EncodeHook(0x1E, RazerKeyboardHookPurpose.Combined));
            RazerKeyboardOwnedHookEvidence.WriteRefusal(evidence);
            Assert.False(TryTerminated(OwnedJournal(), evidence, out _));
            Assert.False(File.Exists(evidence + ".tmp"));
        });
    }

    [Fact]
    public void CleanupShrinksEvidenceBeforeEachClearAndNeverKeepsTheInFlightHook()
    {
        WithDirectory(directory =>
        {
            string evidence = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            byte[] a = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
            byte[] d = RazerSnapTapDriverProtocol.EncodeHook(0x20);
            WriteEvidence(evidence, WorkerId, JournalCreatedAt.AddMinutes(5), a, d);
            var recorded = new List<byte[][]>();
            Action<IReadOnlyList<byte[]>> recorder = RazerKeyboardOwnedHookEvidence.Recorder(evidence, WorkerId);
            var transport = new ListTransport(a, d)
            {
                // Each clear sees the evidence the recorder wrote just before it.
                OnClear = () => recorded.Add(Read(evidence)),
            };
            Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => { }, out _,
                RazerKeyboardHookPurpose.SnapTap, [a, d], recorder));
            Assert.Equal(2, recorded.Count);
            Assert.Equal(new[] { d }, recorded[0]);
            Assert.Empty(recorded[1]);
            // Rewritten evidence still names the dead worker and stays usable.
            Assert.True(TryTerminated(OwnedJournal(KeyboardDriverFeatures.SnapTap), evidence, out var left));
            Assert.Empty(left!);

            // A process ended during the first clear leaves only the hook not yet submitted.
            WriteEvidence(evidence, WorkerId, JournalCreatedAt.AddMinutes(5), a, d);
            transport = new ListTransport(a, d) { FailClear = true };
            Assert.Throws<IOException>(() => SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => { }, out _,
                RazerKeyboardHookPurpose.SnapTap, [a, d], recorder));
            Assert.True(TryTerminated(OwnedJournal(KeyboardDriverFeatures.SnapTap), evidence, out left));
            Assert.Equal(new[] { d }, left);
        });

        static byte[][] Read(string path) =>
            JsonSerializer.Deserialize<RazerKeyboardOwnedHookEvidence.Evidence>(File.ReadAllText(path))!.Hooks;
    }

    [Fact]
    public void RefusedReconciliationLeavesEvidenceUntouched()
    {
        WithDirectory(directory =>
        {
            string evidence = Path.Combine(directory, "keyboard-driver-owned-4242.json");
            byte[] a = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
            WriteEvidence(evidence, WorkerId, JournalCreatedAt.AddMinutes(5), a);
            byte[] original = File.ReadAllBytes(evidence);
            var transport = new ListTransport(a, RazerSnapTapDriverProtocol.EncodeHook(0x20));
            Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => Assert.Fail(), out var failure,
                RazerKeyboardHookPurpose.SnapTap, [a], RazerKeyboardOwnedHookEvidence.Recorder(evidence, WorkerId)));
            Assert.Equal("HookOwnershipChanged", failure);
            Assert.Equal(original, File.ReadAllBytes(evidence));
        });
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(7, false)]
    public void ReaderRejectsAPartialOrInvalidOwnerIdentity(int ownerId, bool withCreatedAt)
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "snaptap-driver-ownership.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(
                CurrentBoot, [], KeyboardDriverFeatures.SnapTap, ownerId, withCreatedAt ? JournalCreatedAt : null)));
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
            File.WriteAllText(path, JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(
                CurrentBoot, [], KeyboardDriverFeatures.SnapTap, null, JournalCreatedAt)));
            Assert.Throws<InvalidDataException>(() => DriverOwnershipJournalReader.Read(path));
        });
    }

    [Fact]
    public void LiveOwnerCheckTreatsALaterStartAsAReusedId()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        DateTimeOffset started = current.StartTime.ToUniversalTime();
        Assert.True(SnapTapDriverSupervisor.IsLiveSince(current.Id, started.AddSeconds(1)));
        Assert.False(SnapTapDriverSupervisor.IsLiveSince(current.Id, started.AddSeconds(-1)));
        Assert.False(SnapTapDriverSupervisor.IsLiveSince(int.MaxValue, DateTimeOffset.UtcNow));
    }

    private static void WriteEvidence(string path, int workerId, DateTimeOffset updatedAt, params byte[][] hooks) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new RazerKeyboardOwnedHookEvidence.Evidence(workerId, updatedAt, hooks)));

    private sealed class ListTransport(params byte[][] hooks) : IRazerSnapTapHookTransport
    {
        internal List<byte[]> Hooks { get; } = hooks.ToList();
        internal int Clears { get; private set; }
        internal Action? OnClear { get; init; }
        internal bool FailClear { get; init; }
        public IReadOnlyList<byte[]> ReadHooks() => Hooks.Select(value => (byte[])value.Clone()).ToArray();
        public void ClearHook(byte[] hook)
        {
            OnClear?.Invoke();
            if (FailClear) { throw new IOException("clear failed after possible submission"); }
            Clears++;
            ushort scan = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hook.AsSpan(0x0A));
            Hooks.RemoveAll(value => value.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(scan)));
        }
        public void SetHook(byte[] hook) => Assert.Fail("Recovery must not install hooks.");
        public void SetHookEnabled(bool enabled) => Assert.Fail("Recovery must not reset aggregate input.");
        public void CloseSubscription() => Assert.Fail("Recovery must never subscribe.");
    }

    private static void Write(string path, KeyboardDriverFeatures features, byte[][]? hooks = null) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(PreviousBoot, hooks ?? [], features)));

    private static void WithDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-driver-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) { File.Delete(file); }
            Directory.Delete(directory);
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// A native process that exits without retiring its owner leaves unsettled
// thermal history. These tests cover classification per progress, the exit
// marker under reacquired custody, the immutable record and proof v4.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeHandbackTests : IDisposable
{
    private readonly string root;

    public NativeHandbackTests()
    {
        root = Environment.GetEnvironmentVariable("OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT") ?? throw new InvalidOperationException("Explicit fixture root required.");
        string sid = Environment.GetEnvironmentVariable("OPENBLADE_THERMAL_FIXTURE_SID") ?? throw new InvalidOperationException("Explicit supplied own SID required.");
        if (!Path.IsPathFullyQualified(root)) { throw new InvalidOperationException("Absolute fixture root required."); }
        WindowsNativeEpochFiles.FixtureFactoryForTests.Value = () => WindowsNativeEpochFiles.CreateSuppliedPrivateFixture(Path.Combine(root, "OpenBlade.NativeEpochFixture-" + Guid.NewGuid().ToString("N")), sid);
    }
    public void Dispose() => WindowsNativeEpochFiles.FixtureFactoryForTests.Value = null;

    private static readonly WindowsNativeTrustLifecycle.OwnerReference Original = new(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static JsonElement Vector(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))).RootElement;
    private static string[] Lines(string journal) => journal.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static void Thermal(WindowsNativeEpochFiles files, byte[] tail) => File.WriteAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal"), tail);
    private static byte[] Performance(string nonce, int last)
    {
        using var stream = new MemoryStream();
        for (int progress = 0; progress <= last; progress++)
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { request = "aaaaaaaa-bbbb-cccc-dddd-000000000001", owner = nonce, user_sid = "S-1-5-21-111-222-333-1002", caller_pid = 42, session = 7, requested = (object?)null, destination = 1, intent = NativeCanonical.Digest("null"u8), progress }));
            stream.WriteByte(10);
        }
        return stream.ToArray();
    }

    // Independent oracle over the journal rows, not the production reader.
    private static (int? Pending, int? Owned, bool Live, int? Prepared) Expected(string[] rows)
    {
        int[] settled = [5, 6, 30, 50, 51, 62, 63, 72, 73];
        using var last = JsonDocument.Parse(rows[^1]);
        int progress = last.RootElement.GetProperty("progress").GetInt32();
        int? owned = null; bool live = false;
        foreach (string raw in rows)
        {
            using var row = JsonDocument.Parse(raw);
            var e = row.RootElement; int p = e.GetProperty("progress").GetInt32();
            if (e.TryGetProperty("fan", out _)) { owned = p == 30 ? 30 : p is 50 or 51 ? null : owned; }
            if (e.TryGetProperty("tfc", out var t) && p is 72 or 73) { live = t.GetProperty("desired").GetProperty("temporary").GetBoolean(); }
        }
        bool open = !settled.Contains(progress);
        return (open && progress != 0 ? progress : null, owned, live, open && progress == 0 ? 0 : null);
    }

    private static void AssertClassified(string[] rows, WindowsNativeHandback handback)
    {
        var (pending, owned, live, prepared) = Expected(rows);
        Assert.Equal(pending, handback.Pending?.Progress);
        Assert.Equal(owned, handback.OwnedFan?.Progress);
        Assert.Equal(live, handback.LiveTemporaryCharge is not null);
        Assert.Equal(prepared, handback.AbandonedPrepared?.Progress);
        Assert.Equal(Original.Epoch, handback.Epoch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PerformanceCrashAtEachProgressIsClassifiedAndStrictReaderStillRefuses(int progress)
    {
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        byte[] journal = Performance(Original.Nonce, progress);
        Thermal(files, journal);
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        AssertClassified(Lines(Encoding.UTF8.GetString(journal)), handback);
        if (progress is >= 1 and <= 4) { Assert.Equal(("performance", "Balanced"), (handback.Pending!.Family, handback.Pending.Performance)); }
        Assert.Equal(progress == 5, handback.IsEmpty);
        if (progress == 5) { _ = WindowsNativeThermalHistory.Read(files, [Original]); }
        else { Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original])); }
    }

    public static IEnumerable<object[]> FamilyPrefixes()
    {
        foreach (string row in Lines(Vector("fan-history-v2.json").GetProperty("released").GetString()!).Select((_, i) => i.ToString(CultureInfo.InvariantCulture)))
        { yield return ["fan-history-v2.json", "released", int.Parse(row, CultureInfo.InvariantCulture)]; }
        foreach (string key in new[] { "standalone", "released" })
        {
            for (int i = 0; i < Lines(Vector("battery-history-v1.json").GetProperty(key).GetString()!).Length; i++) { yield return ["battery-history-v1.json", key, i]; }
        }
        foreach (var item in Vector("temporary-charge-history-v1.json").GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("valid").GetBoolean()))
        {
            for (int i = 0; i < Lines(item.GetProperty("journal").GetString()!).Length; i++) { yield return ["temporary-charge-history-v1.json", item.GetProperty("name").GetString()!, i]; }
        }
    }

    [Theory]
    [MemberData(nameof(FamilyPrefixes))]
    public void FanBatteryAndTemporaryChargeCrashAtEachRowIsClassified(string vector, string key, int last)
    {
        var v = Vector(vector);
        string journal = vector.StartsWith("temporary", StringComparison.Ordinal)
            ? v.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == key).GetProperty("journal").GetString()!
            : v.GetProperty(key).GetString()!;
        string[] rows = Lines(journal)[..(last + 1)];
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        Thermal(files, Bytes(string.Join('\n', rows) + "\n"));
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        AssertClassified(rows, handback);
        if (handback.Pending is { Family: "fan" } fan)
        {
            using var row = JsonDocument.Parse(rows[^1]);
            int mode = row.RootElement.GetProperty("fan").GetProperty("intent").GetProperty("mode").GetInt32();
            // Release progress always targets firmware Automatic.
            Assert.Equal(fan.Progress is 31 or (>= 40 and <= 43) || mode == 0 ? "Automatic" : mode == 1 ? "Fixed" : "Maximum", fan.FanMode);
        }
        if (handback.Pending is { Family: "battery" or "temporaryCharge" } battery)
        {
            using var row = JsonDocument.Parse(rows[^1]);
            var desired = row.RootElement.GetProperty(battery.Family == "battery" ? "battery" : "tfc").GetProperty("desired");
            Assert.Equal(new NativeHandbackBattery(desired.GetProperty("limit").GetInt32(), desired.GetProperty("protection").GetBoolean(), desired.GetProperty("temporary").GetBoolean()), battery.Battery);
        }
    }

    [Fact]
    public void ForeignOwnerStagingAndRolloverStayFenced()
    {
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        Thermal(files, Performance(Original.Nonce, 2));
        // Another owner's unsettled row is never handed back by this exit.
        var other = Original with { Nonce = new string('9', 64), Epoch = new string('4', 32) };
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original, other], other));
        string staging = Path.Combine(files.Root, "owner", "thermal-mutation.journal.pending");
        files.Publish(staging, Performance(Original.Nonce, 3));
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original]));
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        // Staging was never renamed, so its row was never submitted.
        Assert.Equal(2, handback.Pending!.Progress);
        Assert.Equal(NativeCanonical.Digest(File.ReadAllBytes(staging)), handback.StagedJournal);
        File.WriteAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal.rollover"), "partial"u8.ToArray());
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original], Original));
    }

    [Fact]
    public void RecordRoundTripsAndRefusesTampering()
    {
        var item = new NativeHandbackItem("aaaaaaaa-bbbb-cccc-dddd-000000000001", "temporaryCharge", 72, "S-1-5-21-1-2-3-1001", null, null, new(80, true, true));
        var handback = new WindowsNativeHandback(new string('2', 32), null, null, item, null, null);
        byte[] bytes = handback.Serialize([new("S-1-5-21-1-2-3-1001", new string('a', 64))]);
        var (parsed, overrides) = WindowsNativeHandback.Parse(bytes);
        Assert.Equal(handback, parsed);
        Assert.Single(overrides);
        string text = Encoding.UTF8.GetString(bytes);
        foreach (string bad in new[] { text.Replace("\"limit\":80", "\"limit\":81", StringComparison.Ordinal), text.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal), text.Replace("}", ",\"extra\":1}", StringComparison.Ordinal), "{" })
        { Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(Bytes(bad))); }
        Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(new WindowsNativeHandback(new string('2', 32), null, null, null, null, null).Serialize([])));
    }

    [Fact]
    public void RetainedAndFreshProofVersionsCompareEveryThermalField()
    {
        string[] Proof(string header, int count) => ManagedMailboxNativeCustody.ParseRetirement(NativeCanonical.Lines(header,
            [new string('2', 32), .. Enumerable.Range(0, count - 1).Select(i => i == 2 ? "submission-retained" : i == 3 ? "all-metadata-attempts-drained" : new string((char)('a' + i % 6), 64))]));
        string[] v2 = Proof("OpenBlade.NativeRetirementProof.v2", 7), v3 = Proof("OpenBlade.NativeRetirementProof.v3", 8), v4 = Proof("OpenBlade.NativeRetirementProof.v4", 9);
        string empty = v3[7];
        Assert.True(ManagedMailboxNativeCustody.ThermalMatches(v4, v4, empty));
        Assert.True(ManagedMailboxNativeCustody.ThermalMatches(v3, v3, empty));
        Assert.True(ManagedMailboxNativeCustody.ThermalMatches(v2, v2, empty));
        Assert.True(ManagedMailboxNativeCustody.ThermalMatches(v2, v3, empty));
        Assert.False(ManagedMailboxNativeCustody.ThermalMatches(v2, v3, new string('f', 64)));
        // A version change is a different classification in either direction.
        Assert.False(ManagedMailboxNativeCustody.ThermalMatches(v3, v4, empty));
        Assert.False(ManagedMailboxNativeCustody.ThermalMatches(v4, v3, empty));
        Assert.False(ManagedMailboxNativeCustody.ThermalMatches(v2, v4, empty));
        string[] changed = [.. v4[..8], new string('0', 64)];
        Assert.False(ManagedMailboxNativeCustody.ThermalMatches(v4, changed, empty));
    }

    private sealed class Crash : IDisposable
    {
        internal required WindowsNativeEpochFiles Epochs { get; init; }
        internal required WindowsNativeEpochFiles Managed { get; init; }
        internal required IDisposable Packages { get; init; }
        internal required IDisposable Transfer { get; init; }
        internal required NativeEpochRecord Record { get; init; }
        internal required WindowsNativeHandbackWriter Writer { get; init; }
        internal string Attempts => Path.Combine(Epochs.Root, Record.Epoch, "attempts");
        internal string RecordPath => Path.Combine(Writer.Directory, Record.Epoch + ".json");
        public void Dispose() { Transfer.Dispose(); Packages.Dispose(); Managed.Dispose(); Epochs.Dispose(); }
    }

    // The native process launched, became the Active owner, wrote rows and
    // exited without its drain or owner retirement.
    private async Task<Crash> Crashed(int progress, string terminal = "Active")
    {
        var epochs = ManagedMailboxCorrectionTests.Bootstrap(); var managed = ManagedMailboxCorrectionTests.Handoff();
        var packages = await ManagedMailboxCorrectionTests.Packages(mutation: true);
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        var transfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run, '6');
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => managed.RetainFixtureView());
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: storage))
        { record = tx.Record; tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(epochs, record, 17, 1, false); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string nonce = new('4', 64); string? previous = null; ulong sequence = 1;
        foreach (string phase in terminal == "Active" ? new[] { "Reserved", "Active" } : ["Reserved", "Active", "Retiring"])
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phase, sequence.ToString(CultureInfo.InvariantCulture), previous ?? new string('0', 64), record.Installation, record.Epoch, record.ServiceId, "17:1", nonce);
            epochs.Publish(Path.Combine(epochs.Root, "owner", $"owner-{sequence:D20}.bin"), bytes);
            sequence++; previous = NativeCanonical.Digest(bytes);
        }
        Thermal(epochs, Performance(nonce, progress));
        return new Crash
        {
            Epochs = epochs,
            Managed = managed,
            Packages = packages,
            Transfer = transfer,
            Record = record,
            Writer = new WindowsNativeHandbackWriter(Path.Combine(root, "machine-" + Guid.NewGuid().ToString("N"))),
        };
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Retiring")]
    public async Task CrashedNativeHandsBackItsUnsettledWriteBeforeRetirement(string terminal)
    {
        using var crash = await Crashed(2, terminal);
        byte[] journal = File.ReadAllBytes(Path.Combine(crash.Epochs.Root, "owner", "thermal-mutation.journal"));
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default))
        {
            // Without a destination the obligation stays fenced and unretired.
            Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1)).Reason);
            Assert.NotEqual(NativeEpochPhase.Retired, selection.Record.Phase);
            Assert.False(File.Exists(Path.Combine(crash.Epochs.Root, crash.Record.Epoch, "Retired.bin")));
            selection.RetireRecoverableSelection(17, 1, crash.Writer);
            Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        }
        NativeEpochRecord retired;
        using (var reopened = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { retired = reopened.Record; }
        var transfer = ManagedMailboxRecovery.Selected(crash.Managed)!;
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(crash.Epochs.Root, "owner", "thermal-mutation.journal")));
        Assert.True(File.Exists(Path.Combine(crash.Attempts, "exited-17-1.bin")));
        byte[] record = File.ReadAllBytes(crash.RecordPath);
        var handback = WindowsNativeHandback.Parse(record).Handback;
        Assert.Equal((crash.Record.Epoch, 2, "Balanced"), (handback.Epoch, handback.Pending!.Progress, handback.Pending.Performance));
        string proof = Assert.Single(Directory.GetFiles(Path.Combine(crash.Epochs.Root, crash.Record.Epoch, "retirement-proofs")));
        string[] fields = ManagedMailboxNativeCustody.ParseRetirement(File.ReadAllBytes(proof));
        Assert.Equal((9, handback.Commitment, "metadata-attempts-classified"), (fields.Length, fields[8], fields[4]));
        // Later readers accept the retired exit; a rerun re-verifies the same proof.
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(); }
        Assert.Equal(record, File.ReadAllBytes(crash.RecordPath));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView())) { ManagedMailboxRecovery.Resolve(crash.Managed, native, _ => { }); }
        byte[] terminalProof = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(crash.Managed, transfer, "native-proof"));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()))
        { native.RequirePreservedResolution(transfer, retired, terminalProof, "consumed-and-retired"); }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different")]
    public async Task RecordWrittenBeforeACrashIsReusedOnlyWhenIdentical(string existing)
    {
        using var crash = await Crashed(3);
        var pending = new NativeHandbackItem("aaaaaaaa-bbbb-cccc-dddd-000000000001", "performance", existing == "same" ? 3 : 2, "S-1-5-21-111-222-333-1002", "Balanced", null, null);
        crash.Writer.Persist(new WindowsNativeHandback(crash.Record.Epoch, pending, null, null, null, null));
        byte[] before = File.ReadAllBytes(crash.RecordPath);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        if (existing == "same") { selection.RetireRecoverableSelection(17, 1, crash.Writer); Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase); }
        else
        {
            Assert.Equal(NativeRecoveryReason.RetirementPublicationFailed, Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1, crash.Writer)).Reason);
            Assert.False(Directory.Exists(Path.Combine(crash.Epochs.Root, crash.Record.Epoch, "retirement-proofs")));
        }
        Assert.Equal(before, File.ReadAllBytes(crash.RecordPath));
    }

    [Fact]
    public async Task SettledCrashRetiresWithUnchangedV3AndNoRecord()
    {
        using var crash = await Crashed(5);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        selection.RetireRecoverableSelection(17, 1);
        string proof = Assert.Single(Directory.GetFiles(Path.Combine(crash.Epochs.Root, crash.Record.Epoch, "retirement-proofs")));
        Assert.StartsWith("OpenBlade.NativeRetirementProof.v3\n", File.ReadAllText(proof), StringComparison.Ordinal);
        Assert.False(Directory.Exists(crash.Writer.Directory));
    }

    [Fact]
    public async Task HeldCustodyPublishesNoExitAndRetiresNothing()
    {
        using var crash = await Crashed(2);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        using (new FileStream(Path.Combine(crash.Attempts, "custody.lock"), FileMode.Open, FileAccess.Read, FileShare.Read))
        { Assert.Equal(NativeRecoveryReason.ActiveOrUnknownCustody, Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1, crash.Writer)).Reason); }
        Assert.False(File.Exists(Path.Combine(crash.Attempts, "exited-17-1.bin")));
        Assert.False(File.Exists(crash.RecordPath));
        // Readers without custody never create the exit marker themselves.
        Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs)).Reason);
        Assert.False(File.Exists(Path.Combine(crash.Attempts, "exited-17-1.bin")));
    }
}

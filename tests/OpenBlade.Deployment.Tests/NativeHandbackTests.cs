using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// A native process that exits without retiring its owner leaves unsettled
// thermal history. These tests cover classification per progress, the exit
// marker under reacquired custody, the immutable record and proof v4.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class NativeHandbackTests : IDisposable
{
    private readonly string? ownedRoot;

    // The fixture runner supplies both the root and the SID; a plain run uses a
    // fresh own-user temp root, as NativeThermalHistoryTests does.
    public NativeHandbackTests()
    {
        string? root = Environment.GetEnvironmentVariable("OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT");
        string? sid = Environment.GetEnvironmentVariable("OPENBLADE_THERMAL_FIXTURE_SID");
        if (root is null && sid is null)
        {
            ownedRoot = root = Directory.CreateTempSubdirectory("obh").FullName;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            sid = identity.User!.Value;
        }
        if (root is null) { throw new InvalidOperationException("Explicit fixture root required."); }
        if (sid is null) { throw new InvalidOperationException("Explicit supplied own SID required."); }
        if (!Path.IsPathFullyQualified(root)) { throw new InvalidOperationException("Absolute fixture root required."); }
        WindowsNativeEpochFiles.FixtureFactoryForTests.Value = () => WindowsNativeEpochFiles.CreateSuppliedPrivateFixture(Path.Combine(root, "OpenBlade.NativeEpochFixture-" + Guid.NewGuid().ToString("N")), sid);
    }
    public void Dispose()
    {
        WindowsNativeEpochFiles.FixtureFactoryForTests.Value = null;
        if (ownedRoot is null) { return; }
        // Fixture views a test did not dispose still pin directories until finalized.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(ownedRoot, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static readonly WindowsNativeTrustLifecycle.OwnerReference Original = new(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static JsonElement Vector(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))).RootElement;
    private static string[] Lines(string journal) => journal.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static void Thermal(WindowsNativeEpochFiles files, byte[] tail) => File.WriteAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal"), tail);
    private static byte[] Performance(string nonce, int last, string request = "aaaaaaaa-bbbb-cccc-dddd-000000000001")
    {
        using var stream = new MemoryStream();
        for (int progress = 0; progress <= last; progress++)
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { request, owner = nonce, user_sid = "S-1-5-21-111-222-333-1002", caller_pid = 42, session = 7, requested = (object?)null, destination = 1, intent = NativeCanonical.Digest("null"u8), progress }));
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
        // Prepared (0) and release preparation (31) precede every setter.
        bool prepared = open && progress is 0 or 31;
        return (open && !prepared ? progress : null, owned, live, prepared ? progress : null);
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

    // A source cancel retried with fresh IDs (superseded_ids) is handed back
    // like any other cancel: by its progress and desired state, with no new
    // record field. Rows: the enable (0-3), the reserved attempt's Prepared and
    // known-not-submitted rows (4-5), then the fresh retry (6-9). The enable
    // stays live until the retry confirms.
    [Theory]
    [InlineData(4, null, 0, true, "099")]
    [InlineData(5, null, null, true, null)]
    [InlineData(6, null, 0, true, "100")]
    [InlineData(7, 70, null, true, "100")]
    [InlineData(8, 71, null, true, "100")]
    [InlineData(9, null, null, false, null)]
    public void FreshIdCancelRetryCrashIsHandedBackByItsProgress(int last, int? pending, int? prepared, bool live, string? request)
    {
        string journal = Vector("temporary-charge-history-v1.json").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "source-cancel-fresh-ids-after-known-unsent").GetProperty("journal").GetString()!;
        string[] rows = Lines(journal)[..(last + 1)];
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        Thermal(files, Bytes(string.Join('\n', rows) + "\n"));
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        Assert.Equal(pending, handback.Pending?.Progress);
        Assert.Equal(prepared, handback.AbandonedPrepared?.Progress);
        Assert.Equal(live, handback.LiveTemporaryCharge is not null);
        var open = handback.Pending ?? handback.AbandonedPrepared;
        Assert.Equal(request, open?.Request[^3..]);
        if (open is not null)
        {
            // The cancel's target, not the enable's: managed reads the charge off.
            Assert.Equal(("temporaryCharge", new NativeHandbackBattery(80, true, false)), (open.Family, open.Battery));
        }
        // A confirmed retry leaves nothing to hand back, and no record is written.
        Assert.Equal(last == 9, handback.IsEmpty);
        if (!handback.IsEmpty)
        {
            // The record carries no transaction IDs, so it round-trips unchanged.
            var (parsed, _, _) = WindowsNativeHandback.Parse(handback.Serialize([]));
            Assert.Equal(handback, parsed);
        }
    }


    [Fact]
    public void LiveTemporaryChargeOfAnEarlierOwnerIsNotHandedBackByALaterExit()
    {
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        string enable = Vector("temporary-charge-history-v1.json").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "enable").GetProperty("journal").GetString()!;
        var later = Original with { Nonce = new string('9', 64), Epoch = new string('4', 32), Process = "18:2" };
        Thermal(files, [.. Bytes(enable.EndsWith('\n') ? enable : enable + "\n"), .. Performance(later.Nonce, 2)]);
        var handback = WindowsNativeThermalHistory.Read(files, [Original, later], later).Handback!;
        Assert.Equal(2, handback.Pending!.Progress);
        Assert.Null(handback.LiveTemporaryCharge);
        // The earlier owner cannot claim the later owner's open row either.
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original, later], Original));
    }

    private static NativeHandbackItem Item(string family, int progress, string? performance = null, string? fanMode = null, NativeHandbackBattery? battery = null) =>
        new("aaaaaaaa-bbbb-cccc-dddd-000000000001", family, progress, "S-1-5-21-1-2-3-1001", performance, fanMode, battery);

    [Fact]
    public void RecordRoundTripsAndRefusesTampering()
    {
        var handback = new WindowsNativeHandback(new string('2', 32), null, null, Item("temporaryCharge", 72, battery: new(80, true, true)), null, null);
        NativeHandbackEvidence[] evidence = [new("S-1-5-21-1-2-3-1001", new string('a', 64))];
        byte[] bytes = handback.Serialize(evidence);
        var (parsed, overrides, commitment) = WindowsNativeHandback.Parse(bytes);
        Assert.Equal(handback, parsed);
        Assert.Equal(evidence, overrides);
        Assert.Equal(handback.RecordCommitment(evidence), commitment);
        Assert.NotEqual(handback.RecordCommitment([]), commitment);
        string text = Encoding.UTF8.GetString(bytes);
        foreach (string bad in new[]
        {
            text.Replace("\"limit\":80", "\"limit\":81", StringComparison.Ordinal),
            text.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            text.Replace("}", ",\"extra\":1}", StringComparison.Ordinal),
            // The record commits to the override evidence it carries.
            text.Replace(new string('a', 64), new string('b', 64), StringComparison.Ordinal),
            "{",
        })
        { Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(Bytes(bad))); }
        Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(new WindowsNativeHandback(new string('2', 32), null, null, null, null, null).Serialize([])));
    }

    // Custody recomputes the history commitment from native history at every
    // start and compares it with the record. A change to classification, to
    // the commitment lines or to the record encoding would therefore block
    // every handed-back machine, so the format is pinned by a checked-in vector.
    // fromJournal classifies native history; fromRecord covers the remaining
    // slots, a battery policy whose two flags differ and a staged journal.
    [Theory]
    [InlineData("fromJournal")]
    [InlineData("fromRecord")]
    public void HandbackFormatMatchesItsKnownAnswerVector(string name)
    {
        JsonElement v = Vector("native-handback-record-v1.json").GetProperty(name);
        NativeHandbackEvidence[] evidence = [.. v.GetProperty("pendingOverrides").EnumerateArray()
            .Select(e => new NativeHandbackEvidence(e.GetProperty("userSid").GetString()!, e.GetProperty("sha256").GetString()!))];
        var (parsed, overrides, commitment) = WindowsNativeHandback.Parse(Bytes(v.GetProperty("record").GetString()!));
        WindowsNativeHandback handback = parsed;
        if (v.TryGetProperty("journal", out JsonElement journal))
        {
            string[] owner = [.. v.GetProperty("ownerReference").EnumerateArray().Select(e => e.GetString()!)];
            var reference = new WindowsNativeTrustLifecycle.OwnerReference(owner[0], owner[1], owner[2], owner[3], owner[4], owner[5]);
            Assert.Equal(Original, reference);
            using var files = ManagedMailboxCorrectionTests.Bootstrap();
            Thermal(files, Bytes(string.Join('\n', journal.EnumerateArray().Select(e => e.GetString())) + "\n"));
            handback = WindowsNativeThermalHistory.Read(files, [reference], reference).Handback!;
            Assert.Equal(handback, parsed);
        }

        // The literal commitment lines and their digests, independent of the code under test.
        byte[] historyLines = Bytes(v.GetProperty("historyLines").GetString()!);
        byte[] recordLines = Bytes(v.GetProperty("recordLines").GetString()!);
        Assert.Equal(v.GetProperty("history").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(historyLines)));
        Assert.Equal(v.GetProperty("commitment").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(recordLines)));
        Assert.Equal(v.GetProperty("history").GetString(), handback.Commitment);
        Assert.Equal(v.GetProperty("commitment").GetString(), handback.RecordCommitment(evidence));

        byte[] record = handback.Serialize(evidence);
        Assert.Equal(v.GetProperty("record").GetString(), Encoding.UTF8.GetString(record));
        Assert.Equal(v.GetProperty("recordSha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(record)));
        Assert.Equal(evidence, overrides);
        Assert.Equal(v.GetProperty("commitment").GetString(), commitment);
    }

    // The exact descriptor the private-custody recipe issues in product trust
    // mode: private custody accepts it, and so does the ordinary read check
    // the Service uses for the record and its resolution. The namespace's
    // inherited Users read is what private custody excludes.
    [Fact]
    public void ProductPrivateCustodyDescriptorPassesOrdinaryReads()
    {
        var administrators = new System.Security.Principal.SecurityIdentifier("S-1-5-32-544");
        var system = new System.Security.Principal.SecurityIdentifier("S-1-5-18");
        var users = new System.Security.Principal.SecurityIdentifier("S-1-5-32-545");
        System.Security.AccessControl.RawSecurityDescriptor Descriptor(bool usersRead)
        {
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: !usersRead, preserveInheritance: false);
            foreach (var principal in new[] { system, administrators })
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(principal,
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            }
            if (usersRead)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users,
                    System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.AccessControlType.Allow));
            }
            return new(security.GetSecurityDescriptorBinaryForm(), 0);
        }

        var privateDescriptor = Descriptor(usersRead: false);
        Assert.True(WindowsNativeEpochFiles.AcceptsPrivateCustody(privateDescriptor));
        WindowsNativeEpochFiles.RequireProtectedEntry(privateDescriptor);
        var shared = Descriptor(usersRead: true);
        Assert.False(WindowsNativeEpochFiles.AcceptsPrivateCustody(shared));
        WindowsNativeEpochFiles.RequireProtectedEntry(shared);
    }

    // Product trust for an ordinary entry: a trusted owner and no untrusted
    // write access. Accepted: the private recipe, the namespace's inherited
    // Users read, a TrustedInstaller owner, a deny entry and an inherit-only
    // untrusted write. Refused: an untrusted owner, no DACL, and each
    // unprivileged write right.
    [Theory]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)", null)]
    [InlineData("O:BAG:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)", null)]
    [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464G:BAD:P(A;;FA;;;SY)(A;;0x1200a9;;;BU)", null)]
    [InlineData("O:BAG:BAD:P(D;;FA;;;BU)(A;;FA;;;SY)", null)]
    [InlineData("O:BAG:BAD:P(A;OICIIO;FA;;;BU)(A;;FA;;;SY)", null)]
    [InlineData("O:BUG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)", "Native epoch owner or DACL is untrusted.")]
    [InlineData("O:BAG:BA", "Native epoch owner or DACL is untrusted.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x1301bf;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x2;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x4;;;AU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x10;;;WD)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x40;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x100;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x10000;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x40000;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;0x80000;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;GW;;;BU)", "Native epoch permits unprivileged mutation.")]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;GA;;;BU)", "Native epoch permits unprivileged mutation.")]
    public void ProductTrustOrdinaryEntryDescriptors(string sddl, string? refusal)
    {
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor(sddl);
        if (refusal is null) { WindowsNativeEpochFiles.RequireProtectedEntry(descriptor); return; }
        Assert.Equal(refusal, Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.RequireProtectedEntry(descriptor)).Message);
    }

    private static System.Security.AccessControl.RawSecurityDescriptor DescriptorOf(string path) =>
        new(Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()
            : new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);

    private static readonly (string Slot, NativeHandbackItem Item)[] Incoherent =
    [
        ("liveTemporaryCharge", Item("performance", 2, performance: "Balanced")),
        ("liveTemporaryCharge", Item("temporaryCharge", 71, battery: new(80, true, true))),
        ("liveTemporaryCharge", Item("temporaryCharge", 72, battery: new(80, true, false))),
        ("pending", Item("temporaryCharge", 70, battery: new(80, false, true))),
        ("pending", Item("temporaryCharge", 72, battery: new(80, true, true))),
        ("pending", Item("performance", 5, performance: "Balanced")),
        ("pending", Item("performance", 0, performance: "Balanced")),
        ("pending", Item("performance", 7, performance: "Balanced")),
        ("pending", Item("performance", 11, performance: "Custom")),
        ("pending", Item("performance", 79, performance: "BatterySaver")),
        ("pending", Item("performance", 84, performance: "BatteryBalanced")),
        ("pending", Item("performance", 80, performance: "Balanced")),
        ("pending", Item("performance", 83, performance: "Custom")),
        ("abandonedPrepared", Item("performance", 80, performance: "BatterySaver")),
        ("pending", Item("fan", 30, fanMode: "Fixed")),
        ("pending", Item("fan", 31, fanMode: "Automatic")),
        ("pending", Item("fan", 41, fanMode: "Fixed")),
        ("pending", Item("battery", 62, battery: new(80, true, false))),
        ("ownedFan", Item("fan", 30, fanMode: "Automatic")),
        ("ownedFan", Item("fan", 12, fanMode: "Fixed")),
        ("abandonedPrepared", Item("fan", 31, fanMode: "Fixed")),
        ("abandonedPrepared", Item("performance", 2, performance: "Balanced")),
        // Owned HyperBoost's enable pair has no Confirmed, retired or
        // level-setter row to hand back.
        ("pending", Item("hyperBoost", 0)),
        ("pending", Item("hyperBoost", 5)),
        ("pending", Item("hyperBoost", 7)),
        ("pending", Item("hyperBoost", 80)),
        ("ownedFan", Item("hyperBoost", 30)),
    ];
    public static TheoryData<int> IncoherentSlots() => [.. Enumerable.Range(0, Incoherent.Length)];

    [Theory]
    [MemberData(nameof(IncoherentSlots))]
    public void RecordRefusesASlotThatClassificationNeverProduces(int index)
    {
        var (slot, item) = Incoherent[index];
        string epoch = new('2', 32);
        var handback = slot switch
        {
            "pending" => new WindowsNativeHandback(epoch, item, null, null, null, null),
            "ownedFan" => new WindowsNativeHandback(epoch, null, item, null, null, null),
            "liveTemporaryCharge" => new WindowsNativeHandback(epoch, null, null, item, null, null),
            _ => new WindowsNativeHandback(epoch, null, null, null, item, null),
        };
        Assert.False(WindowsNativeHandback.ValidSlot(slot, item) && item.Battery is not { Temporary: true, Protection: false });
        Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(handback.Serialize([])));
    }

    // Custom's CPU and GPU level setters (progress 7-10) are pending Custom rows.
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void CustomLevelSettersArePendingCustomRows(int progress)
    {
        var item = Item("performance", progress, performance: "Custom");
        Assert.True(WindowsNativeHandback.ValidSlot("pending", item));
        var handback = new WindowsNativeHandback(new string('2', 32), item, null, null, null, null);
        Assert.Equal(item, WindowsNativeHandback.Parse(handback.Serialize([])).Handback.Pending);
    }

    // The HyperBoost restore pair before a battery preset (progress 80-83) is a
    // pending row of that battery preset only.
    [Theory]
    [InlineData(80, "BatterySaver")]
    [InlineData(81, "BatteryBalanced")]
    [InlineData(82, "BatterySaver")]
    [InlineData(83, "BatteryBalanced")]
    public void HyperBoostRestoreSettersArePendingBatteryRows(int progress, string performance)
    {
        var item = Item("performance", progress, performance: performance);
        Assert.True(WindowsNativeHandback.ValidSlot("pending", item));
        var handback = new WindowsNativeHandback(new string('2', 32), item, null, null, null, null);
        Assert.Equal(item, WindowsNativeHandback.Parse(handback.Serialize([])).Handback.Pending);
        foreach (string other in new[] { "Silent", "Balanced", "Performance", "Custom" })
        { Assert.False(WindowsNativeHandback.ValidSlot("pending", item with { Performance = other })); }
    }

    // A crash at every row of the HyperBoost-to-battery chain (0, 80-83, 1-4, 5)
    // is classified; only Confirmed passes the strict reader.
    public static TheoryData<int, int> HyperBoostExitPrefixes()
    {
        var data = new TheoryData<int, int>();
        foreach (int destination in new[] { 3, 4 }) { for (int rows = 1; rows <= 10; rows++) { data.Add(destination, rows); } }
        return data;
    }

    [Theory]
    [MemberData(nameof(HyperBoostExitPrefixes))]
    public void HyperBoostExitCrashAtEachRowIsClassifiedAndStrictReaderStillRefuses(int destination, int count)
    {
        int[] chain = [0, 80, 81, 82, 83, 1, 2, 3, 4, 5];
        using var stream = new MemoryStream();
        foreach (int progress in chain[..count])
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { request = "aaaaaaaa-bbbb-cccc-dddd-000000000001", owner = Original.Nonce, user_sid = "S-1-5-21-111-222-333-1002", caller_pid = 42, session = 7, requested = (object?)null, destination, intent = NativeCanonical.Digest("null"u8), progress }));
            stream.WriteByte(10);
        }
        byte[] journal = stream.ToArray();
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        Thermal(files, journal);
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        AssertClassified(Lines(Encoding.UTF8.GetString(journal)), handback);
        int last = chain[count - 1];
        if (last is not (0 or 5)) { Assert.Equal(("performance", destination == 3 ? "BatterySaver" : "BatteryBalanced", last), (handback.Pending!.Family, handback.Pending.Performance, handback.Pending.Progress)); }
        Assert.Equal(last == 5, handback.IsEmpty);
        if (last == 5) { _ = WindowsNativeThermalHistory.Read(files, [Original]); }
        else { Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original])); }
        // A restore row on any other destination is not native chronology.
        string balanced = Encoding.UTF8.GetString(journal).Replace("\"destination\":" + destination.ToString(CultureInfo.InvariantCulture), "\"destination\":1", StringComparison.Ordinal);
        Thermal(files, Bytes(balanced));
        if (count >= 2) { Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original], Original)); }
    }

    // A crash at every row of owned HyperBoost's enable pair (destination 100:
    // 0, 1-4, 5, or 0 then 6) is classified as the hyperBoost family; only a
    // settled row passes the strict reader, and no other chain is native
    // chronology for it.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void OwnedHyperBoostCrashAtEachRowIsClassifiedAndStrictReaderStillRefuses(int count)
    {
        int[] chain = count == 7 ? [0, 6] : new[] { 0, 1, 2, 3, 4, 5 }[..count];
        byte[] Journal(int destination, object? requested, IEnumerable<int> progress)
        {
            using var stream = new MemoryStream();
            byte[] intent = requested is null ? "null"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(requested);
            foreach (int p in progress)
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { request = "aaaaaaaa-bbbb-cccc-eeee-000000000001", owner = Original.Nonce, user_sid = "S-1-5-21-111-222-333-1002", caller_pid = 42, session = 7, requested, destination, intent = NativeCanonical.Digest(intent), progress = p }));
                stream.WriteByte(10);
            }
            return stream.ToArray();
        }
        byte[] journal = Journal(100, null, chain);
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        Thermal(files, journal);
        var handback = WindowsNativeThermalHistory.Read(files, [Original], Original).Handback!;
        AssertClassified(Lines(Encoding.UTF8.GetString(journal)), handback);
        int last = chain[^1];
        if (last is >= 1 and <= 4) { Assert.Equal(new NativeHandbackItem("aaaaaaaa-bbbb-cccc-eeee-000000000001", "hyperBoost", last, "S-1-5-21-111-222-333-1002", null, null, null), handback.Pending); }
        if (last == 0) { Assert.Equal("hyperBoost", handback.AbandonedPrepared!.Family); }
        Assert.Equal(last is 5 or 6, handback.IsEmpty);
        if (last is 5 or 6) { _ = WindowsNativeThermalHistory.Read(files, [Original]); }
        else { Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original])); }
        // The record round-trips the classified item, and an item of the
        // family naming a preset or fan mode is refused.
        if (!handback.IsEmpty) { Assert.Equal(handback, WindowsNativeHandback.Parse(handback.Serialize([])).Handback); }
        foreach (var named in new[] { Item("hyperBoost", 1, performance: "Balanced"), Item("hyperBoost", 2, fanMode: "Automatic") })
        { Assert.Throws<InvalidDataException>(() => WindowsNativeHandback.Parse(new WindowsNativeHandback(new string('2', 32), named, null, null, null, null).Serialize([]))); }
        // The restore and Custom chains are not owned HyperBoost's, and its rows
        // carry no requested override.
        foreach (byte[] foreign in new[]
        {
            Journal(100, null, [0, 80]),
            Journal(100, null, [0, 1, 2, 3, 4, 7]),
            Journal(100, new { value = 1, lifetime = 1, customPerformance = (object?)null }, chain),
        })
        {
            Thermal(files, foreign);
            Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, [Original], Original));
        }
    }

    // Every pair destination native journals hands back under its managed name.
    [Theory]
    [InlineData("Silent")]
    [InlineData("Balanced")]
    [InlineData("Performance")]
    [InlineData("BatterySaver")]
    [InlineData("BatteryBalanced")]
    [InlineData("Custom")]
    public void EveryPerformanceDestinationRoundTripsAsPending(string performance)
    {
        var item = Item("performance", 1, performance: performance);
        var handback = new WindowsNativeHandback(new string('2', 32), item, null, null, null, null);
        Assert.Equal(item, WindowsNativeHandback.Parse(handback.Serialize([])).Handback.Pending);
        var renamed = new WindowsNativeHandback(new string('2', 32), Item("performance", 1, performance: performance + "Mode"), null, null, null, null);
        Assert.ThrowsAny<Exception>(() => WindowsNativeHandback.Parse(renamed.Serialize([])));
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
        internal required ManagedMailboxCorrectionTests.SignedPair Packages { get; init; }
        internal required IDisposable Transfer { get; init; }
        internal required NativeEpochRecord Record { get; init; }
        internal required WindowsNativeHandbackWriter Writer { get; init; }
        internal string Attempts => Path.Combine(Epochs.Root, Record.Epoch, "attempts");
        internal string RecordPath => Path.Combine(Epochs.Root, Record.Epoch, "handback.json");
        internal string ResolutionPath => Path.Combine(Epochs.Root, Record.Epoch, "handback.resolved");
        internal string Generation => Path.Combine(Epochs.Root, Record.Epoch);
        internal string Proofs => Path.Combine(Epochs.Root, Record.Epoch, "retirement-proofs");
        public void Dispose() { Transfer.Dispose(); Packages.Dispose(); Managed.Dispose(); Epochs.Dispose(); }
    }

    private static string[] Snapshot(string directory) => [.. Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
        .Select(p => Path.GetFileName(p) + ":" + Convert.ToBase64String(File.ReadAllBytes(p)))];

    // The native process launched, became the Active owner, wrote rows and
    // exited without its drain or owner retirement.
    private static async Task<Crash> Crashed(int progress, string terminal = "Active")
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
            Writer = new WindowsNativeHandbackWriter(null),
        };
    }

    private static void TransportDrain(Crash crash, uint pid, long created)
    {
        NativeRecoveryTests.AddAttempt(crash.Epochs, crash.Record, pid, created, false);
        PublishDrain(crash, pid, created);
    }

    private static void PublishDrain(Crash crash, uint pid, long created)
    {
        byte[] launch = File.ReadAllBytes(Path.Combine(crash.Attempts, $"launch-{pid}-{created}.bin"));
        crash.Epochs.Publish(Path.Combine(crash.Attempts, $"drain-{pid}-{created}.bin"), NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1",
            crash.Record.Epoch, NativeCanonical.Digest(launch), pid.ToString(CultureInfo.InvariantCulture), created.ToString(CultureInfo.InvariantCulture)));
    }

    private static byte[] ExitMarker(Crash crash, uint pid, long created) => WindowsNativeAttemptHistory.Exited(crash.Record.Epoch,
        File.ReadAllBytes(Path.Combine(crash.Attempts, $"launch-{pid}-{created}.bin")), pid.ToString(CultureInfo.InvariantCulture), created.ToString(CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("Active")]
    [InlineData("Retiring")]
    public async Task CrashedNativeHandsBackItsUnsettledWriteBeforeRetirement(string terminal)
    {
        using var crash = await Crashed(2, terminal);
        byte[] journal = File.ReadAllBytes(Path.Combine(crash.Epochs.Root, "owner", "thermal-mutation.journal"));
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default))
        {
            selection.RetireRecoverableSelection(17, 1, crash.Writer);
            Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        }
        NativeEpochRecord retired;
        using (var reopened = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { retired = reopened.Record; }
        var transfer = ManagedMailboxRecovery.Selected(crash.Managed)!;
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(crash.Epochs.Root, "owner", "thermal-mutation.journal")));
        Assert.Equal(ExitMarker(crash, 17, 1), File.ReadAllBytes(Path.Combine(crash.Attempts, "exited-17-1.bin")));
        byte[] record = File.ReadAllBytes(crash.RecordPath);
        var handback = WindowsNativeHandback.Parse(record).Handback;
        // Private custody: the record names user SIDs and requested settings.
        var generation = DescriptorOf(crash.Generation);
        Assert.True(WindowsNativeEpochFiles.AcceptsPrivateCustody(DescriptorOf(crash.RecordPath), generation.Owner));
        Assert.False(WindowsNativeEpochFiles.AcceptsPrivateCustody(DescriptorOf(Path.Combine(crash.Attempts, "launch-17-1.bin")), generation.Owner));
        Assert.Equal((crash.Record.Epoch, 2, "Balanced"), (handback.Epoch, handback.Pending!.Progress, handback.Pending.Performance));
        string proof = Assert.Single(Directory.GetFiles(crash.Proofs));
        string[] fields = ManagedMailboxNativeCustody.ParseRetirement(File.ReadAllBytes(proof));
        Assert.Equal((9, handback.Commitment, "metadata-attempts-classified"), (fields.Length, fields[8], fields[4]));
        // Later readers accept the retired exit; a rerun re-verifies the same proof.
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(); }
        Assert.Equal(record, File.ReadAllBytes(crash.RecordPath));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()))
        {
            // The Service binds the record to this fresh derivation.
            Assert.Equal(handback.Commitment, Assert.Single(native.Handbacks, pair => pair.Key == crash.Record.Epoch).Value);
            ManagedMailboxRecovery.Resolve(crash.Managed, native, _ => { });
        }
        byte[] terminalProof = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(crash.Managed, transfer, "native-proof"));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()))
        { native.RequirePreservedResolution(transfer, retired, terminalProof, "consumed-and-retired"); }
        // Production startup admission hands the Service this binding and a view
        // of the namespace that holds the record, after native custody ends.
        using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()),
            _ => null, _ => null, _ => null))
        {
            Assert.Equal(handback.Commitment, Assert.Single(startup.NativeHandbacks.Commitments, pair => pair.Key == crash.Record.Epoch).Value);
            using var view = startup.NativeHandbacks.Open!();
            Assert.Equal(record, ManagedMailboxRecovery.Read(view, WindowsNativeHandbackStorage.RecordPath(view, crash.Record.Epoch)));
        }
    }

    // Every refusal leaves native attempt history byte-identical: a marker
    // written before refusal would stop the still-selected generation starting.
    [Theory]
    [InlineData("no-destination", "FirmwareOwnerUnresolved")]
    [InlineData("record-path-occupied", "RetirementPublicationFailed")]
    [InlineData("different-record", "RetirementPublicationFailed")]
    [InlineData("rollover", "FirmwareOwnerUnresolved")]
    [InlineData("second-undrained-launch", "MissingAttemptDrain")]
    [InlineData("drained-but-active", "FirmwareOwnerUnresolved")]
    [InlineData("wrong-exit-marker", "FirmwareOwnerUnresolved")]
    [InlineData("exit-beside-drain", "MissingAttemptDrain")]
    public async Task RefusedRetirementLeavesAttemptHistoryUnchanged(string fault, string refusal)
    {
        using var crash = await Crashed(2);
        WindowsNativeHandbackWriter? writer = crash.Writer;
        switch (fault)
        {
            case "no-destination": writer = null; break;
            case "record-path-occupied": Directory.CreateDirectory(crash.RecordPath); break;
            case "different-record":
                crash.Writer.Persist(crash.Epochs, new WindowsNativeHandback(crash.Record.Epoch, Item("performance", 3, performance: "Balanced"), null, null, null, null));
                break;
            case "rollover":
                File.WriteAllBytes(Path.Combine(crash.Epochs.Root, "owner", "thermal-mutation.journal.rollover"), "partial"u8.ToArray());
                break;
            // SCM restarted the generation and that instance also died undrained.
            case "second-undrained-launch": NativeRecoveryTests.AddAttempt(crash.Epochs, crash.Record, 18, 2, false); break;
            case "drained-but-active": PublishDrain(crash, 17, 1); break;
            case "wrong-exit-marker":
                crash.Epochs.Publish(Path.Combine(crash.Attempts, "exited-17-1.bin"), WindowsNativeAttemptHistory.Exited(crash.Record.Epoch,
                    File.ReadAllBytes(Path.Combine(crash.Attempts, "launch-17-1.bin")), "17", "2"));
                break;
            // A drained restart must not also carry an exit record.
            case "exit-beside-drain":
                TransportDrain(crash, 18, 2);
                crash.Epochs.Publish(Path.Combine(crash.Attempts, "exited-18-2.bin"), ExitMarker(crash, 18, 2));
                break;
        }
        string[] attempts = Snapshot(crash.Attempts);
        string[] records = Snapshot(crash.Generation);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        Assert.Equal(Enum.Parse<NativeRecoveryReason>(refusal), Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1, writer)).Reason);
        Assert.NotEqual(NativeEpochPhase.Retired, selection.Record.Phase);
        Assert.Equal(attempts, Snapshot(crash.Attempts));
        Assert.Equal(records, Snapshot(crash.Generation));
        Assert.False(Directory.Exists(crash.Proofs));
    }

    [Fact]
    public async Task OnlyTheCustodyHolderOfThatEpochReceivesTheExit()
    {
        using var crash = await Crashed(2);
        // Readers and custody of another generation cannot establish this exit.
        Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs)).Reason);
        Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(
            () => WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs, exitedUnderCustody: new string('9', 32))).Reason);
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs, exitedUnderCustody: crash.Record.Epoch))
        {
            Assert.Equal(ExitMarker(crash, 17, 1), owner.PendingExit!.Bytes);
            Assert.Equal(crash.Record.Epoch, owner.Thermal.Handback!.Epoch);
        }
        Assert.False(File.Exists(Path.Combine(crash.Attempts, "exited-17-1.bin")));
    }

    [Fact]
    public async Task RestartedInstanceWithItsDrainIsHandedBackForTheCrashedOwner()
    {
        using var crash = await Crashed(2);
        TransportDrain(crash, 18, 2);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        selection.RetireRecoverableSelection(18, 2, crash.Writer);
        Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        Assert.True(File.Exists(crash.RecordPath));
        Assert.False(File.Exists(Path.Combine(crash.Attempts, "exited-18-2.bin")));
    }

    // A rerun after any crash in the commit sequence completes with the same
    // record, marker and proof.
    [Theory]
    [InlineData("record")]
    [InlineData("proof")]
    [InlineData("marker")]
    public async Task InterruptedCommitSequenceCompletesOnRerun(string completed)
    {
        using var crash = await Crashed(3);
        WindowsNativeHandback expected;
        NativeEpochRecord current;
        using (var opened = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { current = opened.Record; }
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs, exitedUnderCustody: crash.Record.Epoch))
        {
            expected = owner.Thermal.Handback!;
            crash.Writer.Persist(crash.Epochs, expected);
            if (completed is "proof" or "marker")
            {
                byte[] proof = WindowsNativeAttemptHistory.RequireDrained(crash.Epochs, current, Path.Combine(crash.Epochs.Root, crash.Record.Epoch), crash.Packages.Native.Service, owner);
                crash.Epochs.CreateDirectoryOnce(crash.Proofs);
                crash.Epochs.Publish(Path.Combine(crash.Proofs, NativeCanonical.Digest(proof) + ".bin"), proof);
            }
            if (completed == "marker") { owner.PublishExit(crash.Epochs); }
        }
        byte[] record = File.ReadAllBytes(crash.RecordPath);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        selection.RetireRecoverableSelection(17, 1, crash.Writer);
        Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        Assert.Equal(record, File.ReadAllBytes(crash.RecordPath));
        Assert.Equal(ExitMarker(crash, 17, 1), File.ReadAllBytes(Path.Combine(crash.Attempts, "exited-17-1.bin")));
        Assert.Equal(expected.Commitment, ManagedMailboxNativeCustody.ParseRetirement(File.ReadAllBytes(Assert.Single(Directory.GetFiles(crash.Proofs))))[8]);
    }

    [Fact]
    public async Task RecordWrittenBeforeACrashIsReusedWhenIdentical()
    {
        using var crash = await Crashed(3);
        var pending = new NativeHandbackItem("aaaaaaaa-bbbb-cccc-dddd-000000000001", "performance", 3, "S-1-5-21-111-222-333-1002", "Balanced", null, null);
        crash.Writer.Persist(crash.Epochs, new WindowsNativeHandback(crash.Record.Epoch, pending, null, null, null, null));
        byte[] before = File.ReadAllBytes(crash.RecordPath);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        selection.RetireRecoverableSelection(17, 1, crash.Writer);
        Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        Assert.Equal(before, File.ReadAllBytes(crash.RecordPath));
    }

    [Fact]
    public async Task ResolutionThatPredatesItsRecordIsNeverAdopted()
    {
        using var crash = await Crashed(2);
        crash.Epochs.Publish(crash.ResolutionPath, "planted"u8.ToArray());
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        Assert.Equal(NativeRecoveryReason.RetirementPublicationFailed, Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1, crash.Writer)).Reason);
        Assert.False(File.Exists(crash.RecordPath));
        Assert.False(File.Exists(Path.Combine(crash.Attempts, "exited-17-1.bin")));
    }

    [Fact]
    public async Task SettledCrashRetiresWithUnchangedV3AndNoRecord()
    {
        using var crash = await Crashed(5);
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default);
        selection.RetireRecoverableSelection(17, 1);
        string proof = Assert.Single(Directory.GetFiles(crash.Proofs));
        Assert.StartsWith("OpenBlade.NativeRetirementProof.v3\n", File.ReadAllText(proof), StringComparison.Ordinal);
        Assert.False(File.Exists(crash.RecordPath));
        Assert.False(File.Exists(crash.ResolutionPath));
    }

    // Setup writes a record only for the generation its history hands back. A
    // record or resolution beside any other generation fences native custody.
    [Theory]
    [InlineData("handback.json")]
    [InlineData("handback.resolved")]
    public async Task RecordForAGenerationItsHistoryDoesNotHandBackFencesCustody(string name)
    {
        using var crash = await Crashed(5);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()))
        {
            Assert.Empty(native.Handbacks);
            Assert.Same(NativeHandbackBinding.None, native.HandbackBinding);
        }
        crash.Epochs.Publish(Path.Combine(crash.Generation, name), "planted"u8.ToArray());
        Assert.Throws<IOException>(() => ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView()));
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
    }

    // The owner chain is one-way: once a later generation crashes and hands
    // back, an earlier settled generation keeps its exact v3 proof.
    [Fact]
    public async Task HandbackOfALaterGenerationKeepsTheEarlierGenerationProof()
    {
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap(); using var managed = ManagedMailboxCorrectionTests.Handoff();
        using var packages = await ManagedMailboxCorrectionTests.Packages(mutation: true);
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => managed.RetainFixtureView());
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var firstTransfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run, '6');
        NativeEpochRecord first;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: firstTransfer, storage: storage))
        {
            first = tx.Record; tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(epochs, first, 17, 1, false);
            byte[] launch = File.ReadAllBytes(Path.Combine(epochs.Root, first.Epoch, "attempts", "launch-17-1.bin"));
            epochs.Publish(Path.Combine(epochs.Root, first.Epoch, "attempts", "drain-17-1.bin"), NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1", first.Epoch, NativeCanonical.Digest(launch), "17", "1"));
            tx.CommitAfterBoundReadiness(new string('e', 64));
        }
        string firstNonce = new('4', 64); string? previous = null; ulong sequence = 1;
        foreach (string phase in new[] { "Reserved", "Active", "Retiring", "Retired" })
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phase, sequence.ToString(CultureInfo.InvariantCulture), previous ?? new string('0', 64), first.Installation, first.Epoch, first.ServiceId, "17:1", firstNonce);
            epochs.Publish(Path.Combine(epochs.Root, "owner", $"owner-{sequence:D20}.bin"), bytes);
            sequence++; previous = NativeCanonical.Digest(bytes);
        }
        Thermal(epochs, Performance(firstNonce, 5));
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); first = selection.Record; }
        byte[] firstProof;
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(epochs))
        { firstProof = WindowsNativeAttemptHistory.RequireDrained(epochs, first, Path.Combine(epochs.Root, first.Epoch), packages.Native.Service, owner); }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView())) { ManagedMailboxRecovery.Resolve(managed, native, _ => { }); }

        using var secondTransfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run with { Run = new string('9', 32), Process = "18:101" }, 'a');
        NativeEpochRecord second;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: secondTransfer, storage: storage))
        { second = tx.Record; tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(epochs, second, 18, 2, false); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string secondNonce = new('5', 64);
        foreach (string phase in new[] { "Reserved", "Active" })
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phase, sequence.ToString(CultureInfo.InvariantCulture), previous!, second.Installation, second.Epoch, second.ServiceId, "18:2", secondNonce);
            epochs.Publish(Path.Combine(epochs.Root, "owner", $"owner-{sequence:D20}.bin"), bytes);
            sequence++; previous = NativeCanonical.Digest(bytes);
        }
        Thermal(epochs, [.. Performance(firstNonce, 5), .. Performance(secondNonce, 2, "aaaaaaaa-bbbb-cccc-dddd-000000000002")]);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default))
        { selection.RetireRecoverableSelection(18, 2, new WindowsNativeHandbackWriter(null)); }
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(epochs))
        {
            Assert.Equal(second.Epoch, owner.Thermal.Handback!.Epoch);
            byte[] fresh = WindowsNativeAttemptHistory.RequireDrained(epochs, first, Path.Combine(epochs.Root, first.Epoch), packages.Native.Service, owner);
            Assert.StartsWith("OpenBlade.NativeRetirementProof.v3\n", Encoding.ASCII.GetString(fresh), StringComparison.Ordinal);
            Assert.Equal(ManagedMailboxNativeCustody.ParseRetirement(firstProof)[7], ManagedMailboxNativeCustody.ParseRetirement(fresh)[7]);
        }
    }
}

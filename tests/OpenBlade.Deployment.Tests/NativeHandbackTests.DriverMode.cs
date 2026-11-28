using System.Globalization;
using System.Text;

namespace OpenBlade.Deployment.Tests;

// The keyboard Driver-mode record beside a crashed generation's history
// (stage 6a-5 D2 option b). An intent, an unknown record or a leftover stage
// refuses retirement and handback and keeps every byte. Owned and Clear retire
// with the exact handback the history alone gives, and are reported only.
public sealed partial class NativeHandbackTests
{
    private const string CrashedOwner = "4444444444444444444444444444444444444444444444444444444444444444";
    private static string OwnerPath(Crash crash, string name) => Path.Combine(crash.Epochs.Root, "owner", name);
    private static byte[] DriverMode(string state, string prior, string owner = CrashedOwner, ulong sequence = 2) =>
        NativeCanonical.Lines(NativeDriverModeRecord.Header, NativeDriverModeRecord.Contract, sequence.ToString(CultureInfo.InvariantCulture), owner, state, prior, "11,12,13,14");

    private static string[] RootEntries(Crash crash) => [.. Directory.EnumerateFileSystemEntries(crash.Epochs.Root).Order(StringComparer.Ordinal)];
    // Every owner entry, directories included, with its bytes.
    private static string[] OwnerSnapshot(Crash crash) => [.. Directory.EnumerateFileSystemEntries(Path.Combine(crash.Epochs.Root, "owner")).Order(StringComparer.Ordinal)
        .Select(p => Path.GetFileName(p) + ":" + (Directory.Exists(p) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(p))))];

    private static void PlaceDriverMode(Crash crash, string kind)
    {
        string record = OwnerPath(crash, WindowsNativeDriverModeHistory.RecordName);
        string stage = OwnerPath(crash, WindowsNativeDriverModeHistory.StageName);
        switch (kind)
        {
            case "acquire-intent": crash.Epochs.Publish(record, DriverMode("AcquireIntent", "Clear", sequence: 1)); break;
            case "release-intent": crash.Epochs.Publish(record, DriverMode("ReleaseIntent", "Owned", sequence: 3)); break;
            case "corrupt": crash.Epochs.Publish(record, Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(DriverMode("Owned", "Owned")).Replace("\n", "\r\n", StringComparison.Ordinal))); break;
            case "next-version": crash.Epochs.Publish(record, Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(DriverMode("Owned", "Owned")).Replace(".v1\n", ".v2\n", StringComparison.Ordinal))); break;
            case "oversize": crash.Epochs.Publish(record, [.. DriverMode("Owned", "Owned"), .. new byte[NativeDriverModeRecord.MaxBytes]]); break;
            case "foreign-owner": crash.Epochs.Publish(record, DriverMode("Owned", "Owned", owner: new string('a', 64))); break;
            case "record-directory": Directory.CreateDirectory(record); break;
            case "stage": crash.Epochs.Publish(record, DriverMode("Owned", "Owned")); crash.Epochs.Publish(stage, DriverMode("Clear", "Clear", sequence: 3)); break;
            case "stage-directory": crash.Epochs.Publish(record, DriverMode("Owned", "Owned")); Directory.CreateDirectory(stage); break;
            case "stage-only": crash.Epochs.Publish(stage, DriverMode("AcquireIntent", "Clear", sequence: 1)); break;
            case "stage-directory-only": Directory.CreateDirectory(stage); break;
            case "owned": crash.Epochs.Publish(record, DriverMode("Owned", "Owned")); break;
            case "clear": crash.Epochs.Publish(record, DriverMode("Clear", "Clear")); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData("acquire-intent", 2, "acquire intent")]
    [InlineData("acquire-intent", 5, "acquire intent")]
    [InlineData("release-intent", 2, "release intent")]
    [InlineData("release-intent", 5, "release intent")]
    [InlineData("corrupt", 2, "unknown")]
    [InlineData("next-version", 5, "unknown")]
    [InlineData("oversize", 5, "unknown")]
    [InlineData("foreign-owner", 2, "unknown")]
    [InlineData("record-directory", 5, "unknown")]
    [InlineData("stage", 2, "stage")]
    [InlineData("stage-directory", 5, "stage")]
    [InlineData("stage-only", 2, "stage")]
    [InlineData("stage-directory-only", 5, "stage")]
    public async Task DriverModeDoubtRefusesRetirementAndHandbackAndKeepsItsBytes(string kind, int progress, string named)
    {
        using var crash = await Crashed(progress);
        PlaceDriverMode(crash, kind);
        string[] owner = OwnerSnapshot(crash);
        string[] attempts = Snapshot(crash.Attempts);
        string[] records = Snapshot(crash.Generation);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default))
        {
            var refusal = Assert.Throws<NativeRecoveryException>(() => selection.RetireRecoverableSelection(17, 1, crash.Writer));
            Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, refusal.Reason);
            Assert.Contains("keyboard Driver-mode record", refusal.Message, StringComparison.Ordinal);
            Assert.Contains(named, refusal.Message, StringComparison.Ordinal);
            Assert.NotEqual(NativeEpochPhase.Retired, selection.Record.Phase);
        }
        Assert.Equal(owner, OwnerSnapshot(crash));
        Assert.Equal(attempts, Snapshot(crash.Attempts));
        Assert.Equal(records, Snapshot(crash.Generation));
        Assert.False(File.Exists(crash.RecordPath));
        Assert.False(Directory.Exists(crash.Proofs));
    }

    // The handback is the history's own: the record changes no handback byte.
    // The settled record joins the owner commitment the proof attests.
    [Theory]
    [InlineData("owned", "Owned")]
    [InlineData("clear", "Clear")]
    public async Task SettledDriverModeRetiresWithTheHistorysExactHandback(string kind, string finding)
    {
        using var plain = await Crashed(2);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(plain.Epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1, plain.Writer); }
        var expected = WindowsNativeHandback.Parse(File.ReadAllBytes(plain.RecordPath)).Handback;

        using var crash = await Crashed(2);
        PlaceDriverMode(crash, kind);
        string record = OwnerPath(crash, WindowsNativeDriverModeHistory.RecordName);
        byte[] bytes = File.ReadAllBytes(record);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default))
        {
            selection.RetireRecoverableSelection(17, 1, crash.Writer);
            Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        }
        Assert.Equal(bytes, File.ReadAllBytes(record));
        var handback = WindowsNativeHandback.Parse(File.ReadAllBytes(crash.RecordPath)).Handback;
        Assert.Equal(expected with { Epoch = crash.Record.Epoch }, handback);
        Assert.Equal(handback.Serialize([]), File.ReadAllBytes(crash.RecordPath));
        string[] fields = ManagedMailboxNativeCustody.ParseRetirement(File.ReadAllBytes(Assert.Single(Directory.GetFiles(crash.Proofs))));
        Assert.Equal(handback.Commitment, fields[8]);
        string withRecord;
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs))
        {
            Assert.Equal(Enum.Parse<NativeDriverModeFinding>(finding), owner.DriverMode.Finding);
            withRecord = owner.Commitment;
        }
        Assert.Equal(withRecord, fields[6]);
        File.Delete(record);
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(crash.Epochs))
        {
            Assert.Equal(NativeDriverModeFinding.Absent, owner.DriverMode.Finding);
            Assert.Equal(NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerDriverMode.v1", owner.Commitment, NativeCanonical.Digest(bytes))), withRecord);
        }
    }

    // No admitted native build opens owner custody over the record or its
    // stage, so an install is refused before anything is stopped, a settled
    // Clear record included. Only uninstall opens the selection without an
    // incoming package; re-running Install passes one, and there is no Repair.
    [Theory]
    [InlineData("owned", "keyboard Driver-mode record,")]
    [InlineData("clear", "keyboard Driver-mode record,")]
    [InlineData("acquire-intent", "keyboard Driver-mode record,")]
    [InlineData("corrupt", "keyboard Driver-mode record,")]
    [InlineData("stage", "keyboard Driver-mode record stage,")]
    [InlineData("stage-directory", "keyboard Driver-mode record stage,")]
    [InlineData("stage-only", "keyboard Driver-mode record stage,")]
    [InlineData("stage-directory-only", "keyboard Driver-mode record stage,")]
    public async Task IncomingBuildIsRefusedBeforeAnythingStopsOverTheRecord(string kind, string named)
    {
        using var crash = await Crashed(5);
        PlaceDriverMode(crash, kind);
        string[] owner = OwnerSnapshot(crash);
        string[] root = Snapshot(crash.Epochs.Root);
        var refusal = await Assert.ThrowsAsync<NativeRecoveryException>(() =>
            WindowsNativeInstalledSelection.OpenForSetupAsync(crash.Epochs.RetainFixtureView(), default, incoming: crash.Packages.Native));
        Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, refusal.Reason);
        Assert.Contains(named, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(owner, OwnerSnapshot(crash));
        Assert.Equal(root, Snapshot(crash.Epochs.Root));
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(crash.Epochs.RetainFixtureView(), default);
        Assert.NotNull(selection);
    }

    // Begin is the authoritative gate under the epoch writer. Owner admission
    // names a doubt first; a settled record is refused by the build gate.
    [Theory]
    [InlineData("owned", "keyboard Driver-mode record, which the incoming native build cannot open")]
    [InlineData("acquire-intent", "acquire intent")]
    [InlineData("release-intent", "release intent")]
    [InlineData("corrupt", "unknown")]
    [InlineData("stage", "stage")]
    public async Task BeginRefusesANewGenerationOverTheRecord(string kind, string named)
    {
        using var crash = await Crashed(5);
        if (kind == "owned") { PlaceDriverMode(crash, kind); }
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(crash.Epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(crash.Epochs.RetainFixtureView())) { ManagedMailboxRecovery.Resolve(crash.Managed, native, _ => { }); }
        if (kind != "owned") { PlaceDriverMode(crash, kind); }
        var run = new ManagedMailboxRun(new string('9', 32), "18:101", new string('2', 64), new string('3', 64), new string('4', 32));
        using var transfer = ManagedMailboxCorrectionTests.SignedTransfer(crash.Managed, crash.Packages, run, 'a');
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => crash.Managed.RetainFixtureView());
        string[] owner = OwnerSnapshot(crash);
        string[] entries = RootEntries(crash);
        var refusal = Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.Begin(crash.Epochs.RetainFixtureView(), crash.Packages.Native,
            Guid.ParseExact(new string('a', 32), "N"), managedTransfer: transfer, storage: storage));
        Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, refusal.Reason);
        Assert.Contains(named, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(owner, OwnerSnapshot(crash));
        Assert.Equal(entries, RootEntries(crash));
    }

    // The install gate refuses a settled Clear record by its presence alone.
    // That is sound only while packaging refuses keyboard Driver-mode
    // ownership, so no incoming build can open owner custody over the record.
    // A packaging stage that admits the feature fails here until the gate reads
    // the record instead (WindowsNativeDriverModeHistory.RequireAbsentForIncoming).
    [Fact]
    public async Task TheIncomingGateIsPresenceOnlyOnlyWhilePackagingRefusesTheOwner()
    {
        // The features and capabilities build.rs gives a Service with keyboard
        // Driver-mode ownership beside the admitted composition.
        const string admittedFeatures = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        string features = admittedFeatures.Replace("service-mailbox-observation-v1,", "service-keyboard-driver-mode-read-v1,service-keyboard-driver-mode-v1,service-mailbox-observation-v1,", StringComparison.Ordinal);
        string capabilities = admitted + ",service-keyboard-driver-mode-read-v1,service-keyboard-driver-mode-v1";
        string source = new('a', 40);
        // Without the owner the same image is admitted, so a refusal below is the owner's.
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", schema: 2, features: admittedFeatures, capabilities: admitted)], "service", source).StartupPolicy);
        var packaging = Record.Exception(() => NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", schema: 2, features: features, capabilities: capabilities)], "service", source));
        if (packaging is not null) { Assert.Equal("Keyboard Driver-mode ownership is not admitted for packaging.", Assert.IsType<InvalidDataException>(packaging).Message); }

        using var crash = await Crashed(5);
        PlaceDriverMode(crash, "clear");
        var gate = await Record.ExceptionAsync(async () =>
        {
            using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(crash.Epochs.RetainFixtureView(), default, incoming: crash.Packages.Native);
        });
        bool presenceOnly = gate is NativeRecoveryException { Reason: NativeRecoveryReason.FirmwareOwnerUnresolved };
        Assert.False(packaging is null && presenceOnly, "Packaging admits keyboard Driver-mode ownership while the install gate still refuses a settled record by its presence.");
    }
}

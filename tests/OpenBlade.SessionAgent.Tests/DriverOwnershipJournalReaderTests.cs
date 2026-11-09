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

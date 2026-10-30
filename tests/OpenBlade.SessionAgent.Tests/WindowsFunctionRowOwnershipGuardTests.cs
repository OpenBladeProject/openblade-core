using System.Text.Json;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsFunctionRowOwnershipGuardTests
{
    private static readonly string PreviousBoot = DateTime.UtcNow.AddDays(-4).ToString("O");
    private static readonly string CurrentBoot = DateTime.UtcNow.AddDays(-1).ToString("O");

    [Fact]
    public void MissingAndLegacyAdOnlyJournalsNeedNoBootQuery()
    {
        WithDirectory(directory =>
        {
            string ReadBoot() => throw new InvalidOperationException("must not query Windows");
            Assert.Null(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, ReadBoot));
            File.WriteAllText(Path.Combine(directory, "snaptap-driver-ownership.json"), JsonSerializer.Serialize(new
            {
                BootIdentity = PreviousBoot,
                // Legacy SnapTap-only journals may preserve unrelated global hook
                // records; their geometry is the reviewed 292-byte record ABI.
                BaselineHooks = new[] { new byte[RazerSnapTapDriverProtocol.HookRecordLength] },
            }));
            Assert.Null(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, ReadBoot));
        });
    }

    [Theory]
    [InlineData("function-row-driver-ownership.json", KeyboardDriverFeatures.FunctionRow)]
    [InlineData("snaptap-driver-ownership.json", KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow)]
    public void FnOrCombinedJournalBlocksSameBootButLaterBootAllowsWithoutChangingEvidence(string name, KeyboardDriverFeatures features)
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, name);
            string content = JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(PreviousBoot, [], features));
            File.WriteAllText(path, content);
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => PreviousBoot));
            Assert.Null(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
            Assert.Equal(content, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void InvalidJournalBlocksWindowsFn(string content)
    {
        WithDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "function-row-driver-ownership.json"), content);
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => CurrentBoot));
        });
    }

    [Fact]
    public void ReadFailureAndUnknownBootFailClosed()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "function-row-driver-ownership.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(
                PreviousBoot, [], KeyboardDriverFeatures.FunctionRow)));
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => "unknown"));
            Assert.NotNull(WindowsFunctionRowOwnershipGuard.InspectStartup(directory,
                () => throw new IOException("boot read failed")));
        });
    }

    [Fact]
    public void HookBackendSelectionReadsOnlyCachedStartupResult()
    {
        WithDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "function-row-driver-ownership.json"),
                JsonSerializer.Serialize(new SnapTapDriverSupervisor.OwnershipJournal(PreviousBoot, [], KeyboardDriverFeatures.FunctionRow)));
            int reads = 0;
            var failure = WindowsFunctionRowOwnershipGuard.InspectStartup(directory, () => { reads++; return PreviousBoot; });
            for (int index = 0; index < 100; index++)
            {
                Assert.Equal(KeyboardInputBackend.Unknown, WindowsFunctionRowOwnershipGuard.SelectBackend(
                    KeyboardInputBackend.WindowsHook, true, failure));
            }
            Assert.Equal(1, reads);
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-fn-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) { File.Delete(file); }
            Directory.Delete(directory);
        }
    }
}

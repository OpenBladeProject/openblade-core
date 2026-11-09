using System.Text.Json;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapWindowsOwnershipJournalTests
{
    private static readonly string Boot = DateTime.UtcNow.AddDays(-2).ToString("O");

    [Fact]
    public void CleanLifetimeDeletesOnlyItsConfirmedMarker()
    {
        WithPath(path =>
        {
            Assert.True(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => Boot, out var lease, out var failure));
            Assert.Null(failure);
            Assert.True(File.Exists(path));
            Assert.True(lease!.CompleteCleanly(out failure));
            Assert.Null(failure);
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void SameBootOrCrashMarkerBlocksWithoutOverwritingEvidence()
    {
        WithPath(path =>
        {
            Assert.True(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => Boot, out _, out _));
            string original = File.ReadAllText(path);
            Assert.False(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => Boot, out var next, out var failure));
            Assert.Null(next);
            Assert.Equal("WindowsOwnershipRecoveryRequired", failure?.Reason);
            Assert.Equal(original, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not-json")]
    public void InvalidMarkerCannotBeDiscardedEvenOnKnownBoot(string content)
    {
        WithPath(path =>
        {
            File.WriteAllText(path, content);
            Assert.False(SnapTapWindowsOwnershipJournal.ReconcilePrior(path, () => Boot, out _));
            Assert.Equal(content, File.ReadAllText(path));
        });
    }

    [Fact]
    public void KnownLaterBootMayDiscardValidPreviousHelperMarker()
    {
        WithPath(path =>
        {
            DateTime previous = DateTime.UtcNow.AddDays(-5);
            var marker = new SnapTapWindowsOwnershipJournal.Marker(1, previous.ToString("O"), 123,
                previous.AddHours(1), "1.0.0.0", new string('A', 64));
            File.WriteAllText(path, JsonSerializer.Serialize(marker));
            Assert.True(SnapTapWindowsOwnershipJournal.ReconcilePrior(path, () => Boot, out var failure));
            Assert.Null(failure);
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void BootReadFailurePreservesPriorEvidenceAndRefusesAdmission()
    {
        WithPath(path =>
        {
            Assert.True(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => Boot, out _, out _));
            string original = File.ReadAllText(path);
            Assert.False(SnapTapWindowsOwnershipJournal.ReconcilePrior(path,
                () => throw new IOException("boot identity unavailable"), out var failure));
            Assert.Equal("WindowsOwnershipObservationFailed", failure?.Reason);
            Assert.Equal(original, File.ReadAllText(path));
        });
    }

    [Fact]
    public void MissingMarkerNeedsNoBootReadAndUnknownBootCannotCreateMarker()
    {
        WithPath(path =>
        {
            Assert.True(SnapTapWindowsOwnershipJournal.ReconcilePrior(path,
                () => throw new InvalidOperationException("not needed"), out _));
            Assert.False(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => "unknown", out _, out _));
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void ChangedMarkerCannotBeDeletedByCleanCleanup()
    {
        WithPath(path =>
        {
            Assert.True(SnapTapWindowsOwnershipJournal.TryAcquire(path, () => Boot, out var lease, out _));
            File.WriteAllText(path, "{}");
            Assert.False(lease!.CompleteCleanly(out var failure));
            Assert.Equal("WindowsOwnershipMarkerChanged", failure?.Reason);
            Assert.True(File.Exists(path));
        });
    }

    private static void WithPath(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-input-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ownership.json");
        try { action(path); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}

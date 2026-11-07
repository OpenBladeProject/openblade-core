using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class FanStopGuardianStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OpenBlade.GuardianStore.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyPendingEntryFencesStartupIncludingDirectory(bool directory)
    {
        var store = CreateStore();
        Assert.Equal(FanStopStartupDisposition.Clear, store.InspectStartup());
        Directory.CreateDirectory(store.DirectoryPath);
        if (directory) { Directory.CreateDirectory(store.PendingPath); }
        else { File.WriteAllText(store.PendingPath, "malformed"); }
        Assert.Equal(FanStopStartupDisposition.Unresolved, store.InspectStartup());
        Assert.Throws<IOException>(() => store.Reserve(new(Guid.NewGuid(), 123, 456)));
    }

    [Fact]
    public void ReservationIsExactCreateNewAndCompletionRetainsReceipt()
    {
        var store = CreateStore();
        var reservation = new FanStopGuardianReservation(Guid.NewGuid(), 123, 456);
        store.Reserve(reservation);
        Assert.Equal(reservation, store.ReadReservation(reservation.OperationId));
        Assert.Throws<InvalidDataException>(() => store.ReadReservation(Guid.NewGuid()));
        Assert.Throws<IOException>(() => store.Reserve(new(Guid.NewGuid(), 123, 456)));
        store.Finish(reservation.OperationId);
        Assert.False(File.Exists(store.PendingPath));
        string receipt = Path.Combine(store.DirectoryPath, reservation.OperationId.ToString("N") + ".completed.json");
        Assert.Equal(reservation, JsonSerializer.Deserialize<FanStopGuardianReservation>(File.ReadAllText(receipt), FanStopGuardianTransport.Json));
        Assert.Equal(FanStopStartupDisposition.Clear, store.InspectStartup());
    }

    [Fact]
    public void KnownUnlaunchedReservationRetainsSeparateReceipt()
    {
        var store = CreateStore();
        var reservation = new FanStopGuardianReservation(Guid.NewGuid(), 123, 456);
        store.Reserve(reservation);
        store.AbandonBeforeLaunch(reservation.OperationId);
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, reservation.OperationId.ToString("N") + ".not-started.json")));
        Assert.Equal(FanStopStartupDisposition.Clear, store.InspectStartup());
    }

    [Fact]
    public void OwnerResultIsExactDurableAndCannotBeOverwrittenOrClaimProcessExit()
    {
        var store = CreateStore();
        Guid operation = Guid.NewGuid();
        store.Reserve(new(operation, 123, 456));
        var result = new FanStopGuardianSnapshot(operation, 7, FanStopGuardianPhase.Completed,
            FanStopObservedState.Automatic, StopConfirmed: true, AutoConfirmed: true,
            CleanupConfirmed: true, JournalComplete: true, NativeReleased: true);
        Assert.Throws<InvalidDataException>(() => store.RecordOwnerResult(result with { OwnerExited = true }));
        store.RecordOwnerResult(result);
        Assert.Equal(result, store.ReadOwnerResult(operation));
        Assert.Throws<IOException>(() => store.RecordOwnerResult(result));
        Assert.Equal(FanStopStartupDisposition.Unresolved, store.InspectStartup());
        Assert.Throws<InvalidDataException>(() => store.RecordOwnerResult(result with { OperationId = Guid.NewGuid() }));
    }

    [Fact]
    public void VerificationFailureIsUnknownAndCannotReserve()
    {
        Directory.CreateDirectory(root);
        var store = new FanStopGuardianStore(root, (_, _) => throw new UnauthorizedAccessException());
        Assert.Equal(FanStopStartupDisposition.Unknown, store.InspectStartup());
        Assert.Throws<IOException>(() => store.Reserve(new(Guid.NewGuid(), 123, 456)));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    public void MalformedReservationCannotBeFinished(string json)
    {
        var store = CreateStore();
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.PendingPath, json);
        Assert.NotNull(Record.Exception(() => store.Finish(Guid.NewGuid())));
        Assert.True(File.Exists(store.PendingPath));
    }

    private FanStopGuardianStore CreateStore()
    {
        Directory.CreateDirectory(root);
        // Filesystem behavior only: strict production ACL checks are exercised
        // separately, without changing ownership or requiring elevation here.
        return new(root, (_, _) => { });
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }
}

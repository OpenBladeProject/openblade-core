using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal enum CpuTuningOwnershipState { Prepared, PossibleSubmission, OwnedVerified, Restored, Blocked }

internal sealed record CpuSavedTuningStep(
    CpuSavedTuningCommand Command,
    CpuSavedTuningCommand RestoreCommand,
    byte[] Before,
    byte[] After);

// Private service data. Raw firmware payloads must never enter status or logs.
internal sealed record CpuTuningOwnershipJournal(
    int SchemaVersion,
    Guid TransactionId,
    DeviceIdentity Identity,
    CpuTuningSettings Settings,
    byte[] BaselineCurrent,
    byte[] BaselineLast,
    CpuSavedTuningStep[] Steps,
    CpuTuningOwnershipState State,
    int CompletedSteps,
    bool Restoring,
    DateTimeOffset UpdatedAtUtc)
{
    public CpuTuningSettings? PendingUpdateSettings { get; init; }
    public CpuSavedTuningStep? PendingUpdateStep { get; init; }

    internal bool IsWellFormed
    {
        get
        {
            if (SchemaVersion != 1 || TransactionId == Guid.Empty
                || !CpuSavedTuningPlan.IsExactIdentity(Identity)
                || Settings is not { IsWellFormed: true, IsConfigured: true }
                || BaselineCurrent is not { Length: Rz090528CpuFirmwareCodec.SavedConfigurationLength }
                || BaselineLast is not { Length: Rz090528CpuFirmwareCodec.SavedConfigurationLength }
                || !BaselineCurrent.AsSpan().SequenceEqual(BaselineLast)
                || Steps is not { Length: <= 2 }
                || !Enum.IsDefined(State) || CompletedSteps < 0 || CompletedSteps > Steps.Length
                || UpdatedAtUtc == default
                || State == CpuTuningOwnershipState.Prepared && (CompletedSteps != 0 || Restoring || Steps.Length == 0)
                || State == CpuTuningOwnershipState.OwnedVerified && CompletedSteps == 0
                || State == CpuTuningOwnershipState.Restored && (CompletedSteps != 0 || !Restoring)
                || State == CpuTuningOwnershipState.PossibleSubmission
                    && PendingUpdateStep is null
                    && (Restoring ? CompletedSteps == 0 : CompletedSteps == Steps.Length))
            {
                return false;
            }

            if (!CpuSavedSemanticPolicy.MatchesPlan(BaselineCurrent, BaselineLast, Settings, Steps)) { return false; }
            if (PendingUpdateStep is null) { return PendingUpdateSettings is null; }
            if (State != CpuTuningOwnershipState.PossibleSubmission || Restoring
                || PendingUpdateSettings is not { IsConfigured: true, IsWellFormed: true }
                || PendingUpdateStep.Command is not { IsWellFormed: true }
                || PendingUpdateStep.RestoreCommand is not { IsWellFormed: true }
                || PendingUpdateStep.Before is not { Length: 800 } || PendingUpdateStep.After is not { Length: 800 }
                || !PendingUpdateStep.Before.AsSpan().SequenceEqual(ExpectedCurrent)) { return false; }
            CpuSavedTuningStep[]? updatePlan = CpuSavedTuningPlan.Create(BaselineCurrent, PendingUpdateSettings);
            return updatePlan is not null
                && CpuSavedSemanticPolicy.MatchesPlan(BaselineCurrent, BaselineLast, PendingUpdateSettings, updatePlan)
                && CpuSavedSemanticPolicy.MatchesStep(PendingUpdateStep)
                && CpuSavedTuningPlan.Simulate(ExpectedCurrent, PendingUpdateStep.Command).AsSpan().SequenceEqual(PendingUpdateStep.After)
                && CpuSavedTuningPlan.Simulate(PendingUpdateStep.After, PendingUpdateStep.RestoreCommand).AsSpan().SequenceEqual(ExpectedCurrent)
                && (updatePlan.Length == 0 ? BaselineCurrent : updatePlan[^1].After).AsSpan().SequenceEqual(PendingUpdateStep.After);
        }
    }

    internal byte[] ExpectedCurrent => CompletedSteps == 0 ? BaselineCurrent : Steps[CompletedSteps - 1].After;
}

internal sealed record CpuTuningOwnershipJournalLoadResult(
    CpuTuningOwnershipJournal? Journal, bool Invalid, string? Sha256);

internal interface ICpuTuningOwnershipJournalStore
{
    Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token);
    // Null expected hash means create only when absent; never overwrite an
    // unreadable or unexpected journal to recover write authority.
    Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token);
}

internal sealed class CpuTuningOwnershipJournalStore(string path) : ICpuTuningOwnershipJournalStore
{
    private const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string path = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await ReadBytesAsync(token).ConfigureAwait(false);
            if (bytes is null) { return new(null, false, null); }
            CpuTuningOwnershipJournal? journal = JsonSerializer.Deserialize<CpuTuningOwnershipJournal>(bytes, SerializerOptions);
            return journal is { IsWellFormed: true }
                ? new(journal, false, Hash(bytes))
                : new(null, true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(null, true, null);
        }
        finally { gate.Release(); }
    }

    public async Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token)
    {
        if (!journal.IsWellFormed) { throw new ArgumentException("Invalid CPU ownership journal.", nameof(journal)); }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[]? previous = await ReadBytesAsync(token).ConfigureAwait(false);
            if (previous is null ? expectedSha256 is not null : Hash(previous) != expectedSha256) { return null; }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
            if (bytes.Length > MaximumBytes) { return null; }
            // AtomicFile flushes the temporary file through to disk before its
            // atomic replacement. Submission may begin only after this returns.
            await AtomicFile.WriteAsync(path, bytes, token).ConfigureAwait(false);
            return Hash(bytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        finally { gate.Release(); }
    }

    private async Task<byte[]?> ReadBytesAsync(CancellationToken token)
    {
        // Opening directly distinguishes missing from access failures; File.Exists
        // would incorrectly treat an unreadable recovery journal as absent.
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("Invalid CPU journal length."); }
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

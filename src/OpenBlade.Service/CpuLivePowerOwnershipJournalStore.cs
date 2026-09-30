using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Correlation and shape only. Fresh identity/continuity and requested-state
// interpretation remain the independent backend's responsibility.
internal sealed record CpuLivePowerContext(DeviceIdentity Identity, uint EnabledCoreMask,
    uint PmVersion, int PmTableLength, CpuLiveCurveBackendIdentity Backend, CpuLiveCurveBootSequence BootSequence)
{
    internal bool IsWellFormed => CpuSavedTuningPlan.IsExactIdentity(Identity)
        && EnabledCoreMask == CpuLiveCurveSnapshot.SupportedMask
        && PmVersion == CpuLivePowerSnapshot.SupportedVersion && PmTableLength == CpuLivePowerSnapshot.PayloadLength
        && Backend is { IsPinned: true } && BootSequence is { IsWellFormed: true };
}

// Explicit public DTO fields: Protocol value properties are internal and must
// not be serialized implicitly. Exact reconstruction rejects normalization.
internal sealed record CpuLivePowerStoredValue(uint Milliwatts, uint RawBits)
{
    internal CpuLivePowerValue? Decode() => CpuLivePowerValue.TryFromRawBits(RawBits, out var value)
        && value.Milliwatts == Milliwatts ? value : null;
    internal static CpuLivePowerStoredValue From(CpuLivePowerValue value) => new(value.Milliwatts, value.RawBits);
}

internal sealed record CpuLivePowerStoredPair(CpuLivePowerStoredValue Fast, CpuLivePowerStoredValue Slow, uint StapmRawBits)
{
    internal CpuLivePowerPair? Decode() => Fast?.Decode() is { } fast && Slow?.Decode() is { } slow
        && float.IsFinite(BitConverter.UInt32BitsToSingle(StapmRawBits)) ? new(fast, slow, StapmRawBits) : null;
    internal static CpuLivePowerStoredPair From(CpuLivePowerPair pair) => new(
        CpuLivePowerStoredValue.From(pair.Fast), CpuLivePowerStoredValue.From(pair.Slow), pair.StapmRawBits);
}

internal enum CpuLivePowerOwnershipState { Prepared, PossibleSubmission, OwnedVerified, Restored, Blocked }
internal sealed record CpuLivePowerStoredStep(CpuLivePowerLimit Limit, CpuLivePowerStoredValue Before, CpuLivePowerStoredValue After)
{
    internal CpuLivePowerCommand Command()
    {
        if (!CpuLivePowerCommand.TryCreate(Limit, After.Milliwatts, out var command))
        { throw new InvalidDataException("Invalid live PPT step."); }
        return command;
    }
}

internal sealed class CpuLivePowerOwnershipJournal
{
    private readonly bool stepsPresent;
    [JsonConstructor]
    public CpuLivePowerOwnershipJournal(int schemaVersion, Guid transactionId, CpuLivePowerContext context,
        CpuLivePowerStoredPair originalBaseline, CpuLivePowerStoredPair operationBaseline,
        IReadOnlyList<CpuLivePowerStoredStep> steps, CpuLivePowerStoredPair expected,
        CpuLivePowerOwnershipState state, int completedSteps, bool restoring, DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion; TransactionId = transactionId; Context = context;
        OriginalBaseline = originalBaseline; OperationBaseline = operationBaseline;
        stepsPresent = steps is not null;
        Steps = Array.AsReadOnly(steps?.ToArray() ?? []); Expected = expected;
        State = state; CompletedSteps = completedSteps; Restoring = restoring; UpdatedAtUtc = updatedAtUtc;
    }

    public int SchemaVersion { get; }
    public Guid TransactionId { get; }
    public CpuLivePowerContext Context { get; }
    public CpuLivePowerStoredPair OriginalBaseline { get; }
    public CpuLivePowerStoredPair OperationBaseline { get; }
    public IReadOnlyList<CpuLivePowerStoredStep> Steps { get; }
    public CpuLivePowerStoredPair Expected { get; }
    public CpuLivePowerOwnershipState State { get; }
    public int CompletedSteps { get; }
    public bool Restoring { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    internal bool IsWellFormed
    {
        get
        {
            if (SchemaVersion != 1 || TransactionId == Guid.Empty || Context is not { IsWellFormed: true }
                || UpdatedAtUtc == default || !Enum.IsDefined(State) || !stepsPresent || Steps.Count > 2
                || CompletedSteps < 0 || CompletedSteps > Steps.Count
                || OriginalBaseline?.Decode() is not { } original || OperationBaseline?.Decode() is not { } baseline
                || Expected?.Decode() is not { } expected || original.StapmRawBits != baseline.StapmRawBits
                || expected.StapmRawBits != original.StapmRawBits) { return false; }
            foreach (var limit in new[] { CpuLivePowerLimit.Fast, CpuLivePowerLimit.Slow })
            {
                var value = baseline.Get(limit);
                if (value != original.Get(limit) && !IsForward(value.Milliwatts)) { return false; }
            }
            int previous = -1;
            foreach (var step in Steps)
            {
                if (step is null || !Enum.IsDefined(step.Limit) || (int)step.Limit <= previous
                    || step.Before?.Decode() is not { } before || step.After?.Decode() is not { } after
                    || before == after || baseline.Get(step.Limit) != before
                    || (Restoring ? after != original.Get(step.Limit) : !IsForward(after.Milliwatts))) { return false; }
                previous = (int)step.Limit;
            }
            if (ExpectedAt(CompletedSteps) != expected
                || Steps.Count == 0 && (State != CpuLivePowerOwnershipState.Restored || !Restoring)
                || Restoring && ExpectedAt(Steps.Count) != original) { return false; }
            return State switch
            {
                CpuLivePowerOwnershipState.Prepared => CompletedSteps == 0 && Steps.Count > 0,
                CpuLivePowerOwnershipState.PossibleSubmission => CompletedSteps < Steps.Count,
                CpuLivePowerOwnershipState.OwnedVerified => CompletedSteps > 0 && (CompletedSteps < Steps.Count || expected != original),
                CpuLivePowerOwnershipState.Restored => CompletedSteps == Steps.Count && expected == original,
                CpuLivePowerOwnershipState.Blocked => true,
                _ => false,
            };
        }
    }

    private static bool IsForward(uint milliwatts) => milliwatts is >= 45000 and <= 95000 && milliwatts % 1000 == 0;
    internal CpuLivePowerPair ExpectedAt(int completed)
    {
        var pair = OperationBaseline.Decode() ?? throw new InvalidDataException("Invalid live PPT baseline.");
        foreach (var step in Steps.Take(completed)) { pair = pair.With(step.Command()); }
        return pair;
    }
    internal CpuLivePowerOwnershipJournal Progress(CpuLivePowerOwnershipState state, int completed) =>
        new(SchemaVersion, TransactionId, Context, OriginalBaseline, OperationBaseline, Steps,
            CpuLivePowerStoredPair.From(ExpectedAt(completed)), state, completed, Restoring, DateTimeOffset.UtcNow);

    internal static CpuLivePowerOwnershipJournal Prepare(CpuLivePowerContext context, Rz090528CpuLivePowerPlan plan,
        CpuLivePowerOwnershipJournal? owned = null) =>
        new(1, owned?.TransactionId ?? Guid.NewGuid(), context, owned?.OriginalBaseline ?? CpuLivePowerStoredPair.From(plan.Baseline.Pair),
            CpuLivePowerStoredPair.From(plan.Baseline.Pair), plan.Steps.Select(step => new CpuLivePowerStoredStep(step.Command.Limit,
                CpuLivePowerStoredValue.From(step.Before.Get(step.Command.Limit)), CpuLivePowerStoredValue.From(step.After.Get(step.Command.Limit)))).ToArray(),
            CpuLivePowerStoredPair.From(plan.Baseline.Pair), CpuLivePowerOwnershipState.Prepared, 0, false, DateTimeOffset.UtcNow);

    internal CpuLivePowerOwnershipJournal ArmRestoration()
    {
        var current = Expected.Decode()!; var original = OriginalBaseline.Decode()!;
        var steps = new[] { CpuLivePowerLimit.Fast, CpuLivePowerLimit.Slow }.Where(limit => current.Get(limit) != original.Get(limit))
            .Select(limit => new CpuLivePowerStoredStep(limit, CpuLivePowerStoredValue.From(current.Get(limit)),
                CpuLivePowerStoredValue.From(original.Get(limit)))).ToArray();
        return new(SchemaVersion, TransactionId, Context, OriginalBaseline, Expected, steps, Expected,
            steps.Length == 0 ? CpuLivePowerOwnershipState.Restored : CpuLivePowerOwnershipState.Prepared,
            0, true, DateTimeOffset.UtcNow);
    }
}

internal sealed record CpuLivePowerJournalLoadResult(CpuLivePowerOwnershipJournal? Journal, bool Invalid, string? Sha256);
internal interface ICpuLivePowerOwnershipJournalStore
{
    Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token);
    Task<string?> TryWriteAsync(string? expectedSha256, CpuLivePowerOwnershipJournal journal, CancellationToken token);
}

internal sealed class CpuLivePowerOwnershipJournalStore(string path) : ICpuLivePowerOwnershipJournalStore
{
    private const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    private readonly string path = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await ReadBytesAsync(token).ConfigureAwait(false);
            if (bytes is null) { return new(null, false, null); }
            var journal = Decode(bytes);
            return journal is null ? new(null, true, null) : new(journal, false, Hash(bytes));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(null, true, null); }
        finally { gate.Release(); }
    }

    public async Task<string?> TryWriteAsync(string? expectedSha256, CpuLivePowerOwnershipJournal journal, CancellationToken token)
    {
        if (!journal.IsWellFormed) { throw new ArgumentException("Invalid live PPT journal.", nameof(journal)); }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Serialize CAS across store instances/processes as well as this
            // instance. Contention fails closed; no unbounded lock retry.
            using var exclusion = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            byte[]? previous = await ReadBytesAsync(token).ConfigureAwait(false);
            if (previous is null ? expectedSha256 is not null : Hash(previous) != expectedSha256 || Decode(previous) is null) { return null; }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
            if (bytes.Length > MaximumBytes) { return null; }
            await AtomicFile.WriteAsync(path, bytes, token).ConfigureAwait(false);
            return Hash(bytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
        finally { gate.Release(); }
    }

    private static CpuLivePowerOwnershipJournal? Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (!UniqueKeys(document.RootElement)) { return null; }
        var journal = JsonSerializer.Deserialize<CpuLivePowerOwnershipJournal>(bytes, Options);
        return journal is { IsWellFormed: true } ? journal : null;
    }

    private static bool UniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!seen.Add(property.Name) || !UniqueKeys(property.Value)) { return false; } }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (var item in element.EnumerateArray()) { if (!UniqueKeys(item)) { return false; } } }
        return true;
    }

    private async Task<byte[]?> ReadBytesAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("Invalid live PPT journal length."); }
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

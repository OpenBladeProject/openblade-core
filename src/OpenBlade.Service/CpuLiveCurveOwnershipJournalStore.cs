using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record CpuLiveCurveBackendIdentity(string DriverSha256, string PlatformSha256, string DeviceSha256)
{
    internal bool IsPinned => DriverSha256 == Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DriverPath]
        && PlatformSha256 == Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Platform.dll"]
        && DeviceSha256 == Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"];
}

// BootId must be supplied by a trusted fresh backend, never fabricated from a
// timestamp or recovered from this journal. There is no native provider here.
internal sealed record CpuLiveCurveContext(DeviceIdentity Identity, uint EnabledCoreMask,
    uint PmVersion, int PmTableLength, CpuLiveCurveBackendIdentity Backend, Guid BootId)
{
    internal bool IsWellFormed => CpuSavedTuningPlan.IsExactIdentity(Identity)
        && EnabledCoreMask == CpuLiveCurveSnapshot.SupportedMask
        && PmVersion == CpuLiveCurveSnapshot.SupportedPmVersion
        && PmTableLength == CpuLiveCurveSnapshot.SupportedPmLength
        && Backend is { IsPinned: true } && BootId != Guid.Empty;
}

internal enum CpuLiveCurveOwnershipState { Prepared, PossibleSubmission, OwnedVerified, Restored, Blocked }
internal sealed record CpuLiveCurveStoredStep(int PhysicalCoreId, short Before, short After)
{
    internal CpuLiveCurveCommand Command(bool restoring)
    {
        if (!CpuLiveCurveCommand.TryCreate(PhysicalCoreId, restoring ? Before : After,
            CpuLiveCurveSnapshot.SupportedMask, out var command)) { throw new InvalidDataException("Invalid live CO step."); }
        return command;
    }
}

internal sealed class CpuLiveCurveOwnershipJournal
{
    [JsonConstructor]
    public CpuLiveCurveOwnershipJournal(int schemaVersion, Guid transactionId, CpuLiveCurveContext context,
        IReadOnlyList<CpuLiveCoreOffset> originalBaseline, IReadOnlyList<CpuLiveCoreOffset> operationBaseline,
        IReadOnlyList<CpuLiveCurveStoredStep> steps,
        IReadOnlyList<CpuLiveCoreOffset> expected, CpuLiveCurveOwnershipState state, int completedSteps,
        bool restoring, DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion; TransactionId = transactionId; Context = context;
        OriginalBaseline = Array.AsReadOnly(originalBaseline?.ToArray() ?? []);
        OperationBaseline = Array.AsReadOnly(operationBaseline?.ToArray() ?? []);
        Steps = Array.AsReadOnly(steps?.ToArray() ?? []);
        Expected = Array.AsReadOnly(expected?.ToArray() ?? []);
        State = state; CompletedSteps = completedSteps; Restoring = restoring; UpdatedAtUtc = updatedAtUtc;
    }

    public int SchemaVersion { get; }
    public Guid TransactionId { get; }
    public CpuLiveCurveContext Context { get; }
    public IReadOnlyList<CpuLiveCoreOffset> OriginalBaseline { get; }
    public IReadOnlyList<CpuLiveCoreOffset> OperationBaseline { get; }
    public IReadOnlyList<CpuLiveCurveStoredStep> Steps { get; }
    public IReadOnlyList<CpuLiveCoreOffset> Expected { get; }
    public CpuLiveCurveOwnershipState State { get; }
    public int CompletedSteps { get; }
    public bool Restoring { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    internal bool IsWellFormed
    {
        get
        {
            if (SchemaVersion != 2 || TransactionId == Guid.Empty || Context is not { IsWellFormed: true }
                || UpdatedAtUtc == default || !Enum.IsDefined(State) || Steps.Count > 12
                || CompletedSteps < 0 || CompletedSteps > Steps.Count
                || Snapshot(OriginalBaseline) is not { } original || !original.CoreOffsets.SequenceEqual(OriginalBaseline)
                || Snapshot(OperationBaseline) is not { } baseline || !baseline.CoreOffsets.SequenceEqual(OperationBaseline)
                || Snapshot(Expected) is not { } expected || !expected.CoreOffsets.SequenceEqual(Expected)) { return false; }
            // Values different from the original can only originate from the
            // bounded forward editor. Original signed16 values remain intact.
            if (OperationBaseline.Where((core, index) => core.Offset != OriginalBaseline[index].Offset)
                .Any(core => core.Offset is < -30 or > 0)
                || Restoring && !OperationBaseline.SequenceEqual(OriginalBaseline)
                || Steps.Count == 0 && State != CpuLiveCurveOwnershipState.Restored) { return false; }
            int previousId = -1;
            foreach (var step in Steps)
            {
                if (step is null || !CpuLiveCurveSnapshot.IsEnabled(step.PhysicalCoreId) || step.PhysicalCoreId <= previousId
                    || step.After is < -30 or > 0 || step.Before == step.After
                    || OperationBaseline.Single(core => core.PhysicalCoreId == step.PhysicalCoreId).Offset != step.Before) { return false; }
                previousId = step.PhysicalCoreId;
            }
            if (!ExpectedAt(CompletedSteps).CoreOffsets.SequenceEqual(Expected)) { return false; }
            return State switch
            {
                CpuLiveCurveOwnershipState.Prepared => CompletedSteps == 0 && !Restoring,
                CpuLiveCurveOwnershipState.OwnedVerified => CompletedSteps > 0
                    && (CompletedSteps < Steps.Count || !Expected.SequenceEqual(OriginalBaseline)),
                CpuLiveCurveOwnershipState.Restored => Expected.SequenceEqual(OriginalBaseline)
                    && (Restoring ? CompletedSteps == 0 : CompletedSteps == Steps.Count),
                CpuLiveCurveOwnershipState.PossibleSubmission => Restoring ? CompletedSteps > 0 : CompletedSteps < Steps.Count,
                CpuLiveCurveOwnershipState.Blocked => true,
                _ => false,
            };
        }
    }

    internal static CpuLiveCurveSnapshot? Snapshot(IReadOnlyList<CpuLiveCoreOffset> cores) =>
        CpuLiveCurveSnapshot.TryCreate(new(CpuLiveCurveSnapshot.SupportedMask, CpuLiveCurveSnapshot.SupportedPmVersion,
            CpuLiveCurveSnapshot.SupportedPmLength, cores));

    internal CpuLiveCurveSnapshot ExpectedAt(int completed)
    {
        var snapshot = Snapshot(OperationBaseline) ?? throw new InvalidDataException("Invalid live CO operation baseline.");
        foreach (var step in Steps.Take(completed)) { snapshot = snapshot.WithOffset(step.Command(restoring: false)); }
        return snapshot;
    }

    internal CpuLiveCurveOwnershipJournal Progress(CpuLiveCurveOwnershipState state, int completed, bool restoring) =>
        new(SchemaVersion, TransactionId, Context, OriginalBaseline, OperationBaseline, Steps, ExpectedAt(completed).CoreOffsets,
            state, completed, restoring, DateTimeOffset.UtcNow);

    internal static CpuLiveCurveOwnershipJournal Prepare(CpuLiveCurveContext context, Rz090528CpuLiveCurvePlan plan,
        CpuLiveCurveOwnershipJournal? owned = null) =>
        new(2, owned?.TransactionId ?? Guid.NewGuid(), context, owned?.OriginalBaseline ?? plan.Baseline.CoreOffsets,
            plan.Baseline.CoreOffsets,
            plan.Steps.Select(step => new CpuLiveCurveStoredStep(step.Command.PhysicalCoreId,
                step.RestoreCommand.Offset, step.Command.Offset)).ToArray(), plan.Baseline.CoreOffsets,
            CpuLiveCurveOwnershipState.Prepared, 0, false, DateTimeOffset.UtcNow);

    internal CpuLiveCurveOwnershipJournal ArmRestoration()
    {
        var steps = OriginalBaseline.Zip(Expected)
            .Where(pair => pair.First.Offset != pair.Second.Offset)
            .Select(pair => new CpuLiveCurveStoredStep(pair.First.PhysicalCoreId, pair.First.Offset, pair.Second.Offset)).ToArray();
        return new(SchemaVersion, TransactionId, Context, OriginalBaseline, OriginalBaseline, steps, Expected,
            steps.Length == 0 ? CpuLiveCurveOwnershipState.Restored : CpuLiveCurveOwnershipState.OwnedVerified,
            steps.Length, true, DateTimeOffset.UtcNow);
    }
}

internal sealed record CpuLiveCurveJournalLoadResult(CpuLiveCurveOwnershipJournal? Journal, bool Invalid, string? Sha256);
internal interface ICpuLiveCurveOwnershipJournalStore
{
    Task<CpuLiveCurveJournalLoadResult> LoadAsync(CancellationToken token);
    Task<string?> TryWriteAsync(string? expectedSha256, CpuLiveCurveOwnershipJournal journal, CancellationToken token);
}

internal sealed class CpuLiveCurveOwnershipJournalStore(string path) : ICpuLiveCurveOwnershipJournalStore
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

    public async Task<CpuLiveCurveJournalLoadResult> LoadAsync(CancellationToken token)
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

    public async Task<string?> TryWriteAsync(string? expectedSha256, CpuLiveCurveOwnershipJournal journal, CancellationToken token)
    {
        if (!journal.IsWellFormed) { throw new ArgumentException("Invalid live CO journal.", nameof(journal)); }
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

    private static CpuLiveCurveOwnershipJournal? Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (!UniqueKeys(document.RootElement)) { return null; }
        var journal = JsonSerializer.Deserialize<CpuLiveCurveOwnershipJournal>(bytes, Options);
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
            if (stream.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("Invalid live CO journal length."); }
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

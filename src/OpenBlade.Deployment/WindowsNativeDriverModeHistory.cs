using System.Globalization;

namespace OpenBlade.Deployment;

internal enum NativeDriverModeState { AcquireIntent, Owned, ReleaseIntent, Clear }

// The native Service's keyboard Driver-mode ownership record, with the same
// strict grammar as openblade-domain driver_mode_history.rs: canonical ASCII,
// one LF-terminated field per line (header, contract, sequence, owner, state,
// prior, ids). The native Service is its only writer; Setup only reads it.
internal sealed class NativeDriverModeRecord
{
    internal const string Header = "OpenBlade.NativeDriverMode.v1";
    internal const string Contract = "service-keyboard-driver-mode-v1/rz09-0581-v1";
    internal const int MaxBytes = 2048;
    private NativeDriverModeRecord(ulong sequence, string owner, NativeDriverModeState state, NativeDriverModeState prior, byte[] ids)
    { Sequence = sequence; Owner = owner; State = state; Prior = prior; Ids = ids; }
    internal ulong Sequence { get; }
    // The writing generation's Active owner nonce.
    internal string Owner { get; }
    internal NativeDriverModeState State { get; }
    // What an intent reverts to when known not submitted; a settled record's own state.
    internal NativeDriverModeState Prior { get; }
    // The baseline, setter, readback and spare IDs of the job that wrote the last intent.
    internal IReadOnlyList<byte> Ids { get; }

    internal byte[] Encode() => NativeCanonical.Lines(Header, Contract, Sequence.ToString(CultureInfo.InvariantCulture), Owner,
        State.ToString(), Prior.ToString(), string.Join(',', Ids.Select(id => id.ToString(CultureInfo.InvariantCulture))));

    // Refuses every byte outside the grammar. Only the canonical spelling is
    // admitted: the re-encoding must equal the input exactly.
    internal static NativeDriverModeRecord Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) { throw Denied(); }
        string[] f;
        try { f = NativeCanonical.ParseLines(bytes, Header, 7); }
        catch (InvalidDataException) { throw Denied(); }
        // A settled state is its own prior; an intent reverts to a settled one.
        if (f[0] != Contract || !ulong.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sequence) || sequence == 0
            || !NativeCanonical.IsHex(f[2], 64) || StateNamed(f[3]) is not { } state || StateNamed(f[4]) is not { } prior
            || prior is not (NativeDriverModeState.Owned or NativeDriverModeState.Clear)
            || (state is NativeDriverModeState.Owned or NativeDriverModeState.Clear && state != prior)
            || ParseIds(f[5]) is not { } ids)
        { throw Denied(); }
        var record = new NativeDriverModeRecord(sequence, f[2], state, prior, ids);
        if (!record.Encode().AsSpan().SequenceEqual(bytes)) { throw Denied(); }
        return record;
    }
    // Four distinct IDs a transaction pool can issue (1-254).
    private static byte[]? ParseIds(string field)
    {
        string[] values = field.Split(',');
        if (values.Length != 4) { return null; }
        var ids = new byte[4];
        for (int i = 0; i < ids.Length; i++)
        {
            if (!byte.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out ids[i]) || ids[i] is 0 or 255 || Array.IndexOf(ids, ids[i], 0, i) >= 0)
            { return null; }
        }
        return ids;
    }
    private static NativeDriverModeState? StateNamed(string name) => name switch
    {
        nameof(NativeDriverModeState.AcquireIntent) => NativeDriverModeState.AcquireIntent,
        nameof(NativeDriverModeState.Owned) => NativeDriverModeState.Owned,
        nameof(NativeDriverModeState.ReleaseIntent) => NativeDriverModeState.ReleaseIntent,
        nameof(NativeDriverModeState.Clear) => NativeDriverModeState.Clear,
        _ => null,
    };
    private static InvalidDataException Denied() => new("The keyboard Driver-mode record is invalid or unsupported.");
}

// What owner custody holds under the record's two names.
internal enum NativeDriverModeFinding { Absent, Clear, Owned, AcquireIntent, ReleaseIntent, Unknown, LeftoverStage }

// Setup's reader of owner\keyboard-driver-mode.bin and its .pending stage.
// It never repairs, renames or removes either name: any refusal keeps the
// bytes for the native generation that can resolve them.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeDriverModeHistory
{
    internal const string RecordName = "keyboard-driver-mode.bin";
    internal const string StageName = RecordName + ".pending";
    private static readonly WindowsNativeDriverModeHistory Absent = new(NativeDriverModeFinding.Absent);
    // Only Clear and Owned keep their bytes: no admitted lease holds a doubt.
    private readonly byte[]? settledBytes;
    private WindowsNativeDriverModeHistory(NativeDriverModeFinding finding, byte[]? settledBytes = null)
    { Finding = finding; this.settledBytes = settledBytes; }
    internal NativeDriverModeFinding Finding { get; }
    internal static bool IsNamespace(string name) => name is RecordName or StageName;

    // Caller holds the owner writer, so no native Service is replacing the
    // record. A record that does not parse, or names an owner this history
    // never made Active, is Unknown, as the native reader refuses it.
    internal static WindowsNativeDriverModeHistory Read(WindowsNativeEpochFiles files, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners)
    {
        string owner = Path.Combine(files.Root, "owner");
        // A stage, or a directory of that name, may hold a replace that never
        // finished. It is evidence of doubt whatever the record holds.
        if (Attributes(Path.Combine(owner, StageName)) is not null) { return new(NativeDriverModeFinding.LeftoverStage); }
        string path = Path.Combine(owner, RecordName);
        if (Attributes(path) is not { } attributes) { return Absent; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { return new(NativeDriverModeFinding.Unknown); }
        byte[] bytes;
        using (var held = files.OpenFile(path))
        {
            if (held.Length > NativeDriverModeRecord.MaxBytes) { return new(NativeDriverModeFinding.Unknown); }
            bytes = new byte[held.Length];
            held.ReadExactly(bytes);
        }
        NativeDriverModeRecord record;
        try { record = NativeDriverModeRecord.Parse(bytes); }
        catch (InvalidDataException) { return new(NativeDriverModeFinding.Unknown); }
        if (!owners.Any(o => o.Nonce == record.Owner)) { return new(NativeDriverModeFinding.Unknown); }
        return record.State switch
        {
            NativeDriverModeState.AcquireIntent => new(NativeDriverModeFinding.AcquireIntent),
            NativeDriverModeState.ReleaseIntent => new(NativeDriverModeFinding.ReleaseIntent),
            NativeDriverModeState.Owned => new(NativeDriverModeFinding.Owned, bytes),
            _ => new(NativeDriverModeFinding.Clear, bytes),
        };
    }

    // Retirement, handback and every other owner admission. An intent may have
    // reached firmware and has no managed handback item yet, so it refuses, as
    // a record or stage Setup cannot read does. Owned and Clear pass.
    internal void RequireSettled()
    {
        string? doubt = Finding switch
        {
            NativeDriverModeFinding.AcquireIntent => "holds an acquire intent that may have set Driver mode",
            NativeDriverModeFinding.ReleaseIntent => "holds a release intent that may have set Normal mode",
            NativeDriverModeFinding.Unknown => "is unknown, corrupt or names no Active owner",
            NativeDriverModeFinding.LeftoverStage => "has a stage left by an unfinished replace",
            _ => null,
        };
        if (doubt is not null)
        { throw new NativeRecoveryException(NativeRecoveryReason.FirmwareOwnerUnresolved, $"The native keyboard Driver-mode record {doubt}; retirement and handback were refused and the record was preserved."); }
    }

    // A settled record joins the owner commitment, so a retirement proof
    // attests it. History without the record keeps its exact commitment.
    internal string Commit(string ownerCommitment) => settledBytes is null ? ownerCommitment
        : NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerDriverMode.v1", ownerCommitment, NativeCanonical.Digest(settledBytes)));

    // A native build without keyboard Driver-mode ownership refuses to open
    // owner custody that holds either name (durable_owner.rs load_history).
    // WindowsNativePackage refuses that feature for packaging, so no incoming
    // build can open it: refuse before anything is stopped or installed.
    // Presence only, so a running native Service's replace is never disturbed.
    // That is sound only while the packaging refusal stands; a test fails if
    // packaging admits the feature while this gate is presence-only
    // (NativeHandbackTests.TheIncomingGateIsPresenceOnlyOnlyWhilePackagingRefusesTheOwner).
    internal static void RequireAbsentForIncoming(WindowsNativeEpochFiles files)
    {
        string owner = Path.Combine(files.Root, "owner");
        string? found = Attributes(Path.Combine(owner, StageName)) is not null ? "record stage"
            : Attributes(Path.Combine(owner, RecordName)) is not null ? "record" : null;
        if (found is not null)
        { throw new NativeRecoveryException(NativeRecoveryReason.FirmwareOwnerUnresolved, $"Native owner custody holds a keyboard Driver-mode {found}, which the incoming native build cannot open; it was not installed and the {found} was preserved."); }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

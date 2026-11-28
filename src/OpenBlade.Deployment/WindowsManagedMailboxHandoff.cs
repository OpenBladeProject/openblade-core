using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

internal sealed record ManagedMailboxRun(string Run, string Process, string Executable, string Assembly, string Module)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxRun.v1", Run, Process, Executable, Assembly, Module);
    internal static ManagedMailboxRun Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxRun.v1", 6);
        var process = f[1].Split(':');
        if (!NativeCanonical.IsHex(f[0], 32) || f[0].All(c => c == '0') || !NativeCanonical.IsHex(f[2], 64) || !NativeCanonical.IsHex(f[3], 64)
            || !NativeCanonical.IsHex(f[4], 32) || process.Length != 2
            || !uint.TryParse(process[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) || pid == 0
            || !long.TryParse(process[1], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0
            || f[1] != pid.ToString(CultureInfo.InvariantCulture) + ":" + created.ToString(CultureInfo.InvariantCulture))
        { throw new InvalidDataException("Managed mailbox run differs."); }
        return new(f[0], f[1], f[2], f[3], f[4]);
    }
}

internal sealed record ManagedMailboxRequest(string RunDigest, string Installation, string Request, string Package)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxRequest.v1", RunDigest, Installation, Request, Package);
    internal static ManagedMailboxRequest Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxRequest.v1", 5);
        if (!NativeCanonical.IsHex(f[0], 64) || !NativeCanonical.IsHex(f[1], 32) || !NativeCanonical.IsHex(f[2], 32) || !NativeCanonical.IsHex(f[3], 64))
        { throw new InvalidDataException("Managed mailbox request differs."); }
        return new(f[0], f[1], f[2], f[3]);
    }
}

internal sealed record ManagedMailboxBuildBinding(string RunDigest, string Installation, string Package, string Enrollment)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxBuildBinding.v1", RunDigest, Installation, Package, Enrollment);
    internal static ManagedMailboxBuildBinding Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxBuildBinding.v1", 5);
        if (!NativeCanonical.IsHex(f[0], 64) || !NativeCanonical.IsHex(f[1], 32) || !NativeCanonical.IsHex(f[2], 64) || !NativeCanonical.IsHex(f[3], 64))
        { throw new InvalidDataException("Managed authenticated build binding differs."); }
        return new(f[0], f[1], f[2], f[3]);
    }
}

// Product entry owns the same lock later retained by the read-only native lease.
// Files are protected, non-reparse, single-link and held stable through decisions.
internal interface IManagedMailboxRun : IDisposable { void PublishQuiescent(); }

[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedMailboxRun : IManagedMailboxRun
{
    private readonly WindowsNativeEpochFiles files;
    private readonly FileStream exclusion;
    private readonly IDisposable? image;
    internal ManagedMailboxRun Record { get; }
    private bool sealedRun;
    private string installation = string.Empty;
    private WindowsManagedMailboxRun(WindowsNativeEpochFiles files, FileStream exclusion, ManagedMailboxRun record, IDisposable? image)
    { this.files = files; this.exclusion = exclusion; Record = record; this.image = image; }

    internal static bool IsEnrolledImage => Environment.ProcessPath == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade", "OpenBlade.Service.exe");

    // Development/custom-location managed builds retain their existing behavior.
    // If installed migration custody already exists, they must respect it too,
    // but they cannot publish an authenticated installed-run receipt.
    internal static WindowsNativeEpochFiles? OpenOptionalStorage(ManagedMailboxNativeCustody native)
    {
        try { return WindowsNativeEpochFiles.CreateOrOpenManagedHandoff(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            || error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 or 5 or 32 or 33 })
        {
            // Storage that cannot satisfy the creation protocol is unavailable,
            // never adopted. Independent native custody must still be safe.
            native.RequireSafe();
            System.Diagnostics.Trace.TraceWarning("Optional mailbox handoff storage unavailable: {0}", error.Message);
            return null;
        }
    }
    internal static IDisposable? AcquireUnenrolledExclusion(ManagedMailboxNativeCustody native)
    {
        var files = OpenOptionalStorage(native);
        if (files is null) { native.RequireSafe(); return null; }
        return AcquireUnenrolledExclusion(files, native);
    }
    internal static IDisposable AcquireUnenrolledExclusion(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native, Action<ManagedMailboxSelection>? authenticate = null)
    {
        FileStream? exclusion = null;
        try
        {
            exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            ManagedMailboxRecovery.Resolve(files, native, authenticate);
            return new UnenrolledExclusion(files, exclusion);
        }
        catch { exclusion?.Dispose(); files.Dispose(); throw; }
    }
    internal static IDisposable? AcquireLegacyExclusion(ManagedMailboxNativeCustody native) => AcquireLegacyExclusion(native, null);
    internal static IDisposable? AcquireLegacyExclusion(ManagedMailboxNativeCustody native, ManagedMailboxStorage? storage)
    {
        storage = ManagedMailboxStorage.Resolve(native, storage);
        WindowsNativeEpochFiles files;
        try
        {
            if (!storage.LegacyPresent()) { return null; }
            files = storage.OpenLegacy();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidDataException
            || error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 or 5 or 32 or 33 })
        { return null; } // An untrusted leaf cannot admit the old native reader.
        return AcquireLegacyExclusion(files, native);
    }
    internal static IDisposable? AcquireLegacyExclusion(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        Action<ManagedMailboxRun, ManagedMailboxBuildBinding, byte[]>? authenticate = null)
    {
        FileStream? exclusion = null;
        try
        {
            bool history = new[] { "request.bin", "receipt.bin", "mapping.bin", "legacy-native-proof-v2.bin", "legacy-original-exit-contract-v2.bin", "legacy-terminal-intent-v2.bin", "legacy-terminal-v2.bin" }
                .Any(name => ManagedMailboxRecovery.ReadOptional(files, Path.Combine(files.Root, name)) is not null)
                || Directory.EnumerateFiles(files.Root, "read-acquired-*.bin").Any() || Directory.EnumerateFiles(files.Root, "read-retired-*.bin").Any();
            if (ManagedMailboxRecovery.ReadOptional(files, Path.Combine(files.Root, "exclusion.lock")) is null)
            {
                if (history) { throw new IOException("Legacy transfer history is missing its original custody lock; no optional fallback was admitted."); }
                files.Dispose(); return null;
            }
            exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            if (history)
            { ManagedMailboxLegacyRecovery.Resolve(files, native, authenticate); }
            return new UnenrolledExclusion(files, exclusion);
        }
        catch { exclusion?.Dispose(); files.Dispose(); throw; }
    }
    internal static void AuthenticateLegacyTerminal(ManagedMailboxNativeCustody native) => AuthenticateLegacyTerminal(native, null);
    internal static void AuthenticateLegacyTerminal(ManagedMailboxNativeCustody native, ManagedMailboxStorage? storage)
    {
        storage = ManagedMailboxStorage.Resolve(native, storage);
        if (!storage.LegacyPresent()) { return; }
        using var files = storage.OpenLegacy();
        AuthenticateLegacyTerminal(files, native);
    }
    internal static void AuthenticateLegacyTerminal(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        Action<ManagedMailboxRun, ManagedMailboxBuildBinding, byte[]>? authenticate = null)
    {
        // A new managed run retains legacy exclusion until it exits. Pre-stop
        // Setup only validates complete immutable terminals under metadata writer;
        // it never competes for that lifetime exclusion or repairs publication.
        using var writer = files.AcquireWriter();
        bool history = new[] { "request.bin", "receipt.bin", "mapping.bin", "legacy-native-proof-v2.bin", "legacy-original-exit-contract-v2.bin", "legacy-terminal-intent-v2.bin", "legacy-terminal-v2.bin" }
            .Any(name => ManagedMailboxRecovery.ReadOptional(files, Path.Combine(files.Root, name)) is not null);
        if (history) { ManagedMailboxLegacyRecovery.Resolve(files, native, authenticate, terminalOnly: true); }
    }
    private sealed class UnenrolledExclusion(IDisposable files, IDisposable exclusion) : IDisposable
    { public void Dispose() { try { exclusion.Dispose(); } finally { files.Dispose(); } } }

    [RequiresAssemblyFiles("Authenticates the running Service's loaded assemblies against their installed files.")]
    internal static WindowsManagedMailboxRun? Start(WindowsManagedMailboxPackage? incoming, ManagedMailboxNativeCustody native)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade");
        if (!IsEnrolledImage) { throw new IOException("Managed handoff requires its installed Service image."); }
        var files = OpenOptionalStorage(native);
        if (files is null) { return null; }
        FileStream? exclusion = null;
        WindowsManagedMailboxPackage? authenticated = null;
        try
        {
            exclusion = AcquireRunExclusion(files, native);
            if (incoming is not null)
            {
                _ = incoming.AuthenticateLoadedService(directory);
                incoming.Preserve(files);
            }
            string selection = Path.Combine(files.Root, "build-current.bin");
            try { _ = File.GetAttributes(selection); }
            catch (FileNotFoundException) { exclusion.Dispose(); files.Dispose(); return null; }
            using var selected = files.OpenFile(selection);
            var f = NativeCanonical.ParseLines(WindowsNativeEpochFiles.ReadBounded(selected), "OpenBlade.ManagedMailboxBuildSelection.v1", 2);
            authenticated = WindowsManagedMailboxPackage.OpenCached(files, f[0]);
            var identity = authenticated.AuthenticateLoadedService(directory);
            using var process = Process.GetCurrentProcess();
            var record = new ManagedMailboxRun(Guid.NewGuid().ToString("N"), process.Id.ToString(CultureInfo.InvariantCulture) + ":" + process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), identity.Executable, identity.Closure, identity.Module);
            var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(record.Encode()), authenticated.Installation,
                authenticated.PackageDigest, authenticated.EnrollmentDigest);
            return BeginRun(files, exclusion, record, binding, authenticated, authenticated.VerificationProjection);
        }
        catch { authenticated?.Dispose(); exclusion?.Dispose(); files.Dispose(); throw; }
    }

    private static FileStream AcquireRunExclusion(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody? native = null)
    {
        var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        try
        {
            if (native is not null) { ManagedMailboxRecovery.Resolve(files, native); }
            else
            {
                var selected = ManagedMailboxRecovery.Selected(files);
                if (selected is not null) { ManagedMailboxRecovery.ValidateTerminal(files, selected); }
            }
            return exclusion;
        }
        catch { exclusion.Dispose(); throw; }
    }

    private static WindowsManagedMailboxRun BeginRun(WindowsNativeEpochFiles files, FileStream exclusion,
        ManagedMailboxRun record, ManagedMailboxBuildBinding binding, IDisposable? authenticated, byte[]? projection = null)
    {
        // Additive history requires its positive run/binding before publication,
        // and remains durable before the current-run pointer can select this run.
        files.PublishCustody(Path.Combine(files.Root, "run-binding-" + record.Run + ".bin"), binding.Encode());
        files.PublishCustody(Path.Combine(files.Root, "run-" + record.Run + ".bin"), record.Encode());
        if (projection is not null)
        { ManagedMailboxRecovery.PreserveRunHistory(files, record.Encode(), binding.Encode(), projection); }
        files.PublishCurrent(record.Encode());
        return new(files, exclusion, record, authenticated) { installation = binding.Installation };
    }

#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal static WindowsManagedMailboxRun StartForTests(WindowsNativeEpochFiles files, ManagedMailboxRun record, string? installation = null, ManagedMailboxBuildBinding? binding = null, byte[]? projection = null, ManagedMailboxNativeCustody? native = null)
    {
        var exclusion = AcquireRunExclusion(files, native);
        return BeginRun(files, exclusion, record, binding ?? new(NativeCanonical.Digest(record.Encode()), installation ?? new string('5', 32), new string('7', 64), new string('8', 64)), null, projection);
    }
#endif

    // Only called after the tracked scope sealed and successfully drained and the
    // real guardian delegation was independently excluded. No firmware claim.
    internal void PublishQuiescent()
    {
        if (sealedRun) { throw new InvalidOperationException("Managed transport handoff is never replayed."); }
        sealedRun = true;
        using var writer = files.AcquireWriter();
        var selected = ManagedMailboxRecovery.Selected(files);
        if (selected is null || selected.Value.RunDigest != NativeCanonical.Digest(Record.Encode())) { return; }
        // Selected just validated this view under the same metadata writer and
        // retained run exclusion. Only the active-terminal condition remains;
        // rescanning all historical runs here adds no fresh admission.
        WindowsNativeEpochFiles.RequireAbsent(ManagedMailboxRecovery.PathFor(files, selected, "terminal"));
        var request = selected.Value;
        if (request.Installation != installation || !(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(Record.Encode()))
        { throw new IOException("Managed handoff request belongs to another original run/build."); }
        ManagedMailboxRecovery.ExactOrPublish(files, ManagedMailboxRecovery.PathFor(files, selected, "receipt"), ManagedMailboxRecovery.Receipt(selected));
        // Keep exclusion until this original process's host disposes. A restart
        // encounters immutable request.bin and cannot reacquire the mailbox.
    }
    void IManagedMailboxRun.PublishQuiescent() => PublishQuiescent();
    public void Dispose() { exclusion.Dispose(); image?.Dispose(); files.Dispose(); }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedMailboxTransfer : IDisposable, IManagedMailboxReceipt
{
    private readonly WindowsNativeEpochFiles files;
    private readonly byte[] request;
    private readonly byte[] run;
    private readonly ManagedMailboxSelection selected;
    private byte[]? receipt;
    private readonly IDisposable? predecessor;
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal WindowsNativeEpochFiles FilesForTests => files;
#endif
    internal WindowsManagedMailboxTransfer(WindowsNativeEpochFiles files, byte[] run, ManagedMailboxRequest request,
        IDisposable? predecessor = null)
    {
        this.files = files; this.run = run; this.request = request.Encode();
        this.predecessor = predecessor;
        _ = ManagedMailboxRun.Parse(run);
        if (request.RunDigest != NativeCanonical.Digest(run)) { throw new IOException("Managed original run differs."); }
        using var current = files.OpenFile(Path.Combine(files.Root, "run-" + ManagedMailboxRun.Parse(run).Run + ".bin"));
        WindowsNativeEpochFiles.RequireExact(current, run);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(run)) { throw new IOException("Managed run changed before handoff request."); }
        selected = ManagedMailboxRecovery.Create(files, run, request);
    }
    internal void ConfirmOriginalExited()
    {
        // Caller retains and checks the original process object; SCM Stopped is insufficient.
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        ManagedMailboxRecovery.RequireActive(files, selected);
        using var held = files.OpenFile(ManagedMailboxRecovery.PathFor(files, selected, "receipt"));
        receipt = WindowsNativeEpochFiles.ReadBounded(held);
        byte[] expected = NativeCanonical.Lines("OpenBlade.ManagedMailboxQuiescent.v1", NativeCanonical.Digest(request), Convert.ToBase64String(run), "transport-quiescent-no-mutation");
        if (!receipt.AsSpan().SequenceEqual(expected)) { throw new IOException("Managed transport quiescence is unconfirmed."); }
        ManagedMailboxRecovery.ExactOrPublish(files, ManagedMailboxRecovery.PathFor(files, selected, "original-exit"), ManagedMailboxRecovery.OriginalExit(selected));
        // Setup must now be able to rename the predecessor installation. Keep
        // its original signed package evidence, but release installed-tree pins.
        if (predecessor is WindowsManagedMailboxPackage package) { package.ReleaseInstalledPins(); }
    }
    void IManagedMailboxReceipt.ConfirmOriginalExited() => ConfirmOriginalExited();
    // Native writer -> original attempt custody -> owner writer -> managed
    // metadata writer/exclusion. Begin holds this lease before any publication.
    internal BeginLease AcquireForBegin(WindowsNativePackage package, Guid request)
    {
        if (receipt is null) { throw new IOException("Managed handoff was not confirmed against the original process."); }
        var value = selected.Value;
        if (value.Installation != package.Release.Manifest.InstallationId.ToString("N") || value.Request != request.ToString("N")
            || value.Package != NativeCanonical.Digest(package.Record))
        { throw new IOException("Managed handoff incoming request/package changed."); }
        var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        try { RequireBeginActive(); return new(this, exclusion); }
        catch { exclusion.Dispose(); throw; }
    }
    private void RequireBeginActive()
    {
        ManagedMailboxRecovery.RequireActive(files, selected);
        ManagedMailboxRecovery.RequireOriginal(files, selected);
        if (receipt is null || !receipt.AsSpan().SequenceEqual(ManagedMailboxRecovery.Receipt(selected)))
        { throw new IOException("Managed original receipt changed."); }
    }
    internal sealed class BeginLease(WindowsManagedMailboxTransfer transfer, IDisposable exclusion) : IDisposable
    {
        internal void AuthenticatePrefix(ManagedMailboxNativeCustody native) =>
            ManagedMailboxRecovery.AuthenticateRetainedTerminals(transfer.files, native, transfer.selected);
        internal void PublishPreparation(WindowsNativeEpochFiles epochs, NativeEpochRecord epoch)
        {
            transfer.RequireBeginActive();
            byte[] bytes = NativeCanonical.Lines("OpenBlade.ManagedNativePreparation.v2", Convert.ToBase64String(epoch.Encode()),
                Convert.ToBase64String(ManagedMailboxRecovery.Mapping(transfer.selected, epoch)));
            ManagedMailboxRecovery.ExactOrPublish(epochs, Path.Combine(epochs.Root, epoch.Epoch, "preparation.bin"), bytes);
        }
        internal void PublishNativeBinding(WindowsNativeEpochFiles epochs, NativeEpochRecord epoch)
        {
            transfer.RequireBeginActive();
            var value = transfer.selected.Value;
            if (value.Installation != epoch.Installation || value.Request != epoch.Request || value.Package != epoch.Package)
            { throw new IOException("Managed handoff epoch changed."); }
            ManagedMailboxRecovery.ExactOrPublish(epochs, Path.Combine(epochs.Root, epoch.Epoch, "managed-handoff.bin"), ManagedMailboxRecovery.Mapping(transfer.selected, epoch));
        }
        internal void BindEpoch(WindowsNativeEpochFiles epochs, NativeEpochRecord epoch)
        {
            PublishNativeBinding(epochs, epoch);
            var selected = transfer.selected;
            byte[] intent = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpochIntent.v2", NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(epoch.Encode()));
            ManagedMailboxRecovery.ExactOrPublish(transfer.files, ManagedMailboxRecovery.PathFor(transfer.files, selected, "epoch-intent"), intent);
            ManagedMailboxRecovery.ExactOrPublish(transfer.files, ManagedMailboxRecovery.PathFor(transfer.files, selected, "mapping"), ManagedMailboxRecovery.Mapping(selected, epoch));
        }
        public void Dispose() => exclusion.Dispose();
    }
    internal void BindEpoch(WindowsNativeEpochFiles epochs, NativeEpochRecord epoch)
    {
        if (receipt is null) { throw new IOException("Managed handoff was not confirmed against the original process."); }
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        using var lease = new BeginLease(this, exclusion);
        lease.BindEpoch(epochs, epoch);
    }
    public void Dispose() { try { predecessor?.Dispose(); } finally { files.Dispose(); } }
}

internal interface IManagedMailboxReceipt { void ConfirmOriginalExited(); }

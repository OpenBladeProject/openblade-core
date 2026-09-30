using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Service;

// This is a conservative mutation barrier, not ownership, measured fan state,
// or authority to replay commands. The scope identifies a device class, not a serial.
internal sealed record CoolingPadMutationEvidence(int SchemaVersion, string Scope, long Generation,
    Guid Epoch, bool MutationMayHaveOccurred);
internal sealed record CoolingPadMutationEvidenceSnapshot(CoolingPadMutationEvidence? Evidence,
    string? Sha256, bool Invalid = false)
{
    internal bool BlocksAdmission => Invalid || Evidence?.MutationMayHaveOccurred == true;
}
internal interface ICoolingPadMutationEvidenceStore
{
    Task<CoolingPadMutationEvidenceSnapshot> LoadAsync(CancellationToken token);
    Task<CoolingPadMutationEvidenceSnapshot> ArmAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token);
    Task<CoolingPadMutationEvidenceSnapshot> ResolveAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token);
}

internal sealed class InMemoryCoolingPadMutationEvidenceStore : ICoolingPadMutationEvidenceStore
{
    private readonly object gate = new();
    private CoolingPadMutationEvidenceSnapshot current = new(null, null);
    public Task<CoolingPadMutationEvidenceSnapshot> LoadAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (gate) { return Task.FromResult(current); } }
    public Task<CoolingPadMutationEvidenceSnapshot> ArmAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => Change(expected, true, token);
    public Task<CoolingPadMutationEvidenceSnapshot> ResolveAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => Change(expected, false, token);
    private Task<CoolingPadMutationEvidenceSnapshot> Change(CoolingPadMutationEvidenceSnapshot expected, bool armed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            CoolingPadMutationEvidenceStore.RequireExpected(current, expected, armed);
            var evidence = CoolingPadMutationEvidenceStore.Next(expected, armed);
            current = CoolingPadMutationEvidenceStore.Snapshot(evidence);
            return Task.FromResult(current);
        }
    }
}

internal sealed class CoolingPadMutationEvidenceStore : ICoolingPadMutationEvidenceStore
{
    internal const string FileName = "cooling-pad-mutation-evidence.json";
    internal const string ExactScope = "RZ09-0528:RZ09-05289EN4:1532:0F43:0200:MI00:000C:0001:91:report0";
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly string directory;
    private readonly string path;
    private readonly Action<string, bool> verifySecurity;
    private readonly Action<string> protectFile;

    internal CoolingPadMutationEvidenceStore(string machineDataDirectory)
        : this(machineDataDirectory, VerifySecurity, ProtectFile) { }
    // Injectable security operations are test-only. Installed composition always uses the constructor above.
    internal CoolingPadMutationEvidenceStore(string machineDataDirectory, Action<string, bool> verifySecurity, Action<string> protectFile)
    {
        directory = Path.GetFullPath(machineDataDirectory);
        path = Path.Combine(directory, FileName);
        this.verifySecurity = verifySecurity;
        this.protectFile = protectFile;
    }

    public Task<CoolingPadMutationEvidenceSnapshot> LoadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return Task.FromResult(Read()); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Task.FromResult(new CoolingPadMutationEvidenceSnapshot(null, null, Invalid: true)); }
    }
    public Task<CoolingPadMutationEvidenceSnapshot> ArmAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => Change(expected, true, token);
    public Task<CoolingPadMutationEvidenceSnapshot> ResolveAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => Change(expected, false, token);

    private Task<CoolingPadMutationEvidenceSnapshot> Change(CoolingPadMutationEvidenceSnapshot expected, bool armed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var held = Lock();
        RequireExpected(Read(), expected, armed);
        CoolingPadMutationEvidence next = Next(expected, armed);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                protectFile(temporary);
                VerifyPath(temporary, false);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            VerifyPath(directory, true);
            // The exclusive protected lock serializes comparison and replacement across processes.
            File.Move(temporary, path, overwrite: true);
            VerifyPath(path, false);
            using (var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.WriteThrough)) { committed.Flush(flushToDisk: true); }
            var actual = Read();
            if (actual.Evidence != next) { throw new IOException("Pad mutation evidence readback mismatch."); }
            return Task.FromResult(actual);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private FileStream Lock()
    {
        VerifyPath(directory, true);
        string lockPath = path + ".lock";
        bool exists;
        try { _ = File.GetAttributes(lockPath); exists = true; }
        catch (FileNotFoundException) { exists = false; }
        if (exists) { VerifyPath(lockPath, false); }
        var held = new FileStream(lockPath, exists ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!exists) { protectFile(lockPath); }
            VerifyPath(lockPath, false);
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private CoolingPadMutationEvidenceSnapshot Read()
    {
        VerifyPath(directory, true);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new(null, null); }
        VerifyPath(path, false);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("Invalid pad evidence length."); }
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        using (var document = JsonDocument.Parse(bytes))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != document.RootElement.EnumerateObject().Count())
            { throw new InvalidDataException("Duplicate or invalid pad evidence fields."); }
        }
        CoolingPadMutationEvidence? evidence = JsonSerializer.Deserialize<CoolingPadMutationEvidence>(bytes, Json);
        if (evidence is null || evidence.SchemaVersion != 1 || evidence.Scope != ExactScope
            || evidence.Generation <= 0 || evidence.Epoch == Guid.Empty)
        { throw new InvalidDataException("Invalid pad mutation evidence."); }
        return new(evidence, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private void VerifyPath(string candidate, bool isDirectory)
    {
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("Reparse-point pad evidence storage is not admitted."); }
        verifySecurity(candidate, isDirectory);
    }
    internal static void RequireExpected(CoolingPadMutationEvidenceSnapshot current, CoolingPadMutationEvidenceSnapshot expected, bool armed)
    {
        if (current.Invalid || expected.Invalid || current != expected
            || (armed ? current.BlocksAdmission : current.Evidence?.MutationMayHaveOccurred != true))
        { throw new InvalidDataException("Pad evidence changed or is unresolved."); }
    }
    internal static CoolingPadMutationEvidence Next(CoolingPadMutationEvidenceSnapshot expected, bool armed) =>
        new(1, ExactScope, checked((expected.Evidence?.Generation ?? 0) + 1),
            armed ? Guid.NewGuid() : expected.Evidence!.Epoch, armed);
    internal static CoolingPadMutationEvidenceSnapshot Snapshot(CoolingPadMutationEvidence evidence) =>
        new(evidence, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence, Json))));

    private static void ProtectFile(string candidate)
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        { security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow)); }
        new FileInfo(candidate).SetAccessControl(security);
    }
    private static void VerifySecurity(string candidate, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory ? new DirectoryInfo(candidate).GetAccessControl()
            : new FileInfo(candidate).GetAccessControl();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || rules.Length != 2 || !rules.Any(rule => rule.IdentityReference == system)
            || !rules.Any(rule => rule.IdentityReference == administrators)
            || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow || rule.IsInherited
                || (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl
                || rule.PropagationFlags != PropagationFlags.None
                || rule.InheritanceFlags != (isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None)))
        { throw new UnauthorizedAccessException("Pad evidence storage ACL is not protected exactly."); }
    }
}

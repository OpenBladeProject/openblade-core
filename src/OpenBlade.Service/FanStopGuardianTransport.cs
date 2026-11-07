using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum FanStopGuardianCommandKind { Heartbeat = 1, Automatic = 2 }
internal sealed record FanStopGuardianCommand(Guid OperationId, FanStopGuardianCommandKind Kind,
    PowerSource? ObservedPowerSource = null, bool ContinueStop = true);
internal sealed record FanStopGuardianOwnerIdentity(int Pid, long StartUtcTicks);
internal sealed record FanStopGuardianReservation(Guid OperationId, int ServicePid, long ServiceStartUtcTicks);

// A private, SYSTEM-only, process-bound channel. This protocol has no register,
// duty, profile or executable argument and is not a public service endpoint.
internal static class FanStopGuardianTransport
{
    internal const int MaximumFrameBytes = 8192;
    internal static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length is 0 or > MaximumFrameBytes) { throw new InvalidDataException("Guardian frame exceeds its bound."); }
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumFrameBytes) { throw new InvalidDataException("Invalid guardian frame length."); }
        byte[] payload = new byte[count];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Empty guardian frame.");
    }

    internal static void RequireSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || !Environment.Is64BitProcess || !OperatingSystem.IsWindows())
        { throw new UnauthorizedAccessException("The fan guardian requires the installed LocalSystem service."); }
    }

    internal static int PeerPid(PipeStream pipe, bool server)
    {
        uint pid;
        bool success = server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!success || pid is 0 or > int.MaxValue) { throw new IOException("Guardian pipe peer identity is unavailable."); }
        return (int)pid;
    }

    internal static Process RetainPeer(int pid, string executable, long? startTicks = null)
    {
        VerifyProtectedPath(executable, directory: false);
        var peer = Process.GetProcessById(pid);
        try
        {
            _ = peer.SafeHandle; // Retain the original process object, never re-open a recycled PID.
            if (peer.HasExited || peer.SessionId != 0 || !string.Equals(peer.MainModule?.FileName,
                    executable, StringComparison.OrdinalIgnoreCase)
                || (startTicks.HasValue && peer.StartTime.ToUniversalTime().Ticks != startTicks))
            { throw new UnauthorizedAccessException("Guardian peer is not the original installed process."); }
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }

    internal static void VerifyProtectedPath(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        { throw new IOException("Guardian paths must be local absolute paths."); }
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Guardian paths may not traverse a reparse point."); }
        }
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        static bool Trusted(IdentityReference? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544"
            or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))))
        { throw new UnauthorizedAccessException("Guardian storage owner is not trusted."); }
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0
                && !Trusted(rule.IdentityReference))
            { throw new UnauthorizedAccessException("Guardian storage is writable by an untrusted principal."); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

// Presence, malformed content, read failure and an interrupted startup all fence
// new commands. A completed journal is never a capability to resume an old run.
internal sealed class FanStopGuardianStore
{
    private readonly Action<string, bool> verifyPath;

    internal FanStopGuardianStore(string machineDataDirectory)
        : this(machineDataDirectory, FanStopGuardianTransport.VerifyProtectedPath) { }

    internal FanStopGuardianStore(string machineDataDirectory, Action<string, bool> verifyPath)
    {
        this.verifyPath = verifyPath ?? throw new ArgumentNullException(nameof(verifyPath));
        DirectoryPath = Path.Combine(Path.GetFullPath(machineDataDirectory), "FanStopGuardian");
    }

    internal string DirectoryPath { get; }
    internal string PendingPath => Path.Combine(DirectoryPath, "pending.json");
    internal string RunRoot(Guid operation) => Path.Combine(DirectoryPath, operation.ToString("N"));

    internal void RecordOwnerResult(FanStopGuardianSnapshot result)
    {
        _ = ReadReservation(result.OperationId);
        if (result.OwnerExited || result.Phase is not (FanStopGuardianPhase.Completed or FanStopGuardianPhase.Refused))
        { throw new InvalidDataException("Only the owner's terminal result may be recorded here."); }
        string root = RunRoot(result.OperationId);
        Directory.CreateDirectory(root);
        verifyPath(root, true);
        using var file = new FileStream(Path.Combine(root, "owner-result.json"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(file, result, FanStopGuardianTransport.Json);
        file.Flush(true);
    }

    internal FanStopGuardianSnapshot ReadOwnerResult(Guid operation)
    {
        string path = Path.Combine(RunRoot(operation), "owner-result.json");
        verifyPath(path, false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > FanStopGuardianTransport.MaximumFrameBytes)
        { throw new InvalidDataException("Invalid owner result size."); }
        var result = JsonSerializer.Deserialize<FanStopGuardianSnapshot>(file, FanStopGuardianTransport.Json);
        return result is { } value && value.OperationId == operation && !value.OwnerExited
            ? value : throw new InvalidDataException("Owner result identity is invalid.");
    }

    internal FanStopStartupDisposition InspectStartup()
    {
        try
        {
            verifyPath(Path.GetDirectoryName(DirectoryPath)!, true);
            if (!Directory.Exists(DirectoryPath)) { return FanStopStartupDisposition.Clear; }
            verifyPath(DirectoryPath, true);
            // File.Exists hides access errors. Enumerate successfully, then check
            // every pending entry, including a directory occupying the marker.
            return Directory.EnumerateFileSystemEntries(DirectoryPath).Any(path =>
                Path.GetFileName(path).Equals("pending.json", StringComparison.OrdinalIgnoreCase))
                ? FanStopStartupDisposition.Unresolved : FanStopStartupDisposition.Clear;
        }
        catch { return FanStopStartupDisposition.Unknown; }
    }

    internal void Reserve(FanStopGuardianReservation reservation)
    {
        if (reservation.OperationId == Guid.Empty || InspectStartup() != FanStopStartupDisposition.Clear)
        { throw new IOException("An earlier guardian operation needs read-only reconciliation."); }
        Directory.CreateDirectory(DirectoryPath);
        verifyPath(DirectoryPath, true);
        using var file = new FileStream(PendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(file, reservation, FanStopGuardianTransport.Json);
        file.Flush(true);
    }

    internal FanStopGuardianReservation ReadReservation(Guid operation)
    {
        verifyPath(PendingPath, false);
        using var file = new FileStream(PendingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > FanStopGuardianTransport.MaximumFrameBytes) { throw new InvalidDataException("Invalid guardian reservation."); }
        var value = JsonSerializer.Deserialize<FanStopGuardianReservation>(file, FanStopGuardianTransport.Json);
        if (value is null || value.OperationId != operation || value.ServicePid <= 0 || value.ServiceStartUtcTicks <= 0)
        { throw new InvalidDataException("Guardian reservation does not match this operation."); }
        return value;
    }

    internal void Finish(Guid operation)
    {
        _ = ReadReservation(operation);
        // Keep the original durable receipt. The live client alone may archive
        // it after independently observing native release and original owner exit.
        File.Move(PendingPath, Path.Combine(DirectoryPath, operation.ToString("N") + ".completed.json"));
    }

    internal void AbandonBeforeLaunch(Guid operation)
    {
        _ = ReadReservation(operation);
        File.Move(PendingPath, Path.Combine(DirectoryPath, operation.ToString("N") + ".not-started.json"));
    }
}

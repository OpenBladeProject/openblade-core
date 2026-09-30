using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// This is a fixed, transient two-child experiment. Frames are evidence and
// single-step permits on an authenticated live pipe, never a saved resume token.
internal sealed record PadProcessStep(string Name, string Result, int Transaction = 0,
    byte Class = 0, byte Command = 0, string Arguments = "", bool Mutation = false,
    bool Physical = false)
{
    internal RazerCommandSpec Spec()
    {
        byte[] expected = Convert.FromHexString(Result);
        return new(Class, Command, Convert.FromHexString(Arguments), expected.Length,
            payload => payload.Span.SequenceEqual(expected));
    }
}

internal static class PadProcessSequence
{
    internal static IReadOnlyList<PadProcessStep> ForRole(int role) => role switch
    {
        1 => First,
        2 => Second,
        _ => throw new InvalidDataException("Invalid child role."),
    };
    private static readonly PadProcessStep[] First =
    [
        new("open", "opened"),
        new("baseline", "0000", 1, 0, 0x84, "0000"),
        new("acquire", "0300", 2, 0, 4, "0300", true),
        new("candidate", "0300", 3, 0, 0x84, "0000"),
        new("fixed", "01012C", 4, 0x0D, 0x10, "01012C", true),
        new("fixedPhysical", "YES", Physical: true),
        new("close", "FullyDisposed"),
    ];
    private static readonly PadProcessStep[] Second =
    [
        new("open", "opened"),
        new("intermediate", "0300", 5, 0, 0x84, "0000"),
        new("auto", "000600", 6, 0x0D, 0x10, "000600", true),
        new("autoPhysical", "YES", Physical: true),
        new("restore", "0000", 7, 0, 4, "0000", true),
        new("restored", "0000", 8, 0, 0x84, "0000"),
        new("finalPhysical", "YES", Physical: true),
        new("close", "FullyDisposed"),
    ];
}

internal sealed class PadProcessState
{
    private int role;
    private int index;
    private bool awaitingResult;
    private bool permitted;
    private bool receipt;
    private bool exited;
    private bool firstExited;
    internal string? OriginalBaseline { get; private set; }
    internal bool MutationPermitted { get; private set; }
    internal bool Success { get; private set; }
    internal List<string> Evidence { get; } = [];

    internal void Begin(int nextRole)
    {
        Require((role == 0 && nextRole == 1)
            || (role == 1 && firstExited && OriginalBaseline == "0000" && nextRole == 2));
        role = nextRole;
        index = 0;
        receipt = false;
        exited = false;
        awaitingResult = false;
    }
    internal PadProcessStep Request(string frame)
    {
        Require(role != 0 && !awaitingResult && !receipt);
        var steps = PadProcessSequence.ForRole(role);
        Require(index < steps.Count);
        var step = steps[index];
        Require(frame == $"READY:{role}:{step.Name}");
        // The original baseline must have reached the supervisor before any set.
        Require(!step.Mutation || OriginalBaseline == "0000");
        awaitingResult = true;
        permitted = false;
        return step;
    }
    internal void Permit(PadProcessStep step)
    {
        Require(awaitingResult && !permitted && step == PadProcessSequence.ForRole(role)[index]);
        permitted = true;
        MutationPermitted |= step.Mutation;
        Evidence.Add($"PERMIT:{role}:{step.Name}");
    }
    internal void Complete(string frame)
    {
        Require(awaitingResult && permitted);
        var step = PadProcessSequence.ForRole(role)[index];
        Require(frame == $"DONE:{role}:{step.Name}:{step.Result}");
        if (role == 1 && step.Name == "baseline") { OriginalBaseline = step.Result; }
        Evidence.Add(frame);
        awaitingResult = false;
        index++;
    }
    internal void Receipt(string frame)
    {
        Require(!awaitingResult && !receipt && index == PadProcessSequence.ForRole(role).Count
            && frame == $"COMPLETE:{role}:FullyDisposed");
        receipt = true;
    }
    internal void Exited(int exitCode)
    {
        Require(receipt && !exited && exitCode == 0);
        exited = true;
        if (role == 1) { firstExited = true; }
        else { Success = true; }
        Evidence.Add($"EXIT:{role}:0");
    }
    private static void Require(bool condition)
    {
        if (!condition) { throw new InvalidDataException("Process trial phase or evidence rejected."); }
    }
}

internal sealed class PadProcessChannel(PipeStream pipe)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal async Task WriteAsync(string value, CancellationToken token)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length is < 1 or > 512) { throw new InvalidDataException("Frame size rejected."); }
        byte[] frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }
    internal async Task<string> ReadAsync(CancellationToken token)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 512) { throw new InvalidDataException("Frame size rejected."); }
        byte[] bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Utf8.GetString(bytes);
    }
    internal async Task ExpectAsync(string value, CancellationToken token)
    {
        if (await ReadAsync(token).ConfigureAwait(false) != value)
        { throw new InvalidDataException("Process trial frame rejected."); }
    }
}

internal sealed record PadProcessBuild(string AppHost, string Digest)
{
    internal static PadProcessBuild Read()
    {
        string host = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Missing apphost."));
        string dll = Path.GetFullPath(typeof(PadProcessBuild).Assembly.Location);
        if (!Path.GetFileName(host).Equals("OpenBlade.Capture.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(dll).Equals("OpenBlade.Capture.dll", StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(host)!.Equals(Path.GetDirectoryName(dll), StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Use the fixed local OpenBlade.Capture apphost."); }
        using var hostStream = File.OpenRead(host);
        using var dllStream = File.OpenRead(dll);
        Guid loadedModule = typeof(PadProcessBuild).Assembly.ManifestModule.ModuleVersionId;
        RequireLoadedModule(dllStream, loadedModule);
        dllStream.Position = 0;
        string digest = Convert.ToHexString(SHA256.HashData(hostStream))
            + Convert.ToHexString(SHA256.HashData(dllStream)) + ":" + loadedModule.ToString("N");
        return new(host, digest);
    }
    internal static void RequireLoadedModule(Stream diskAssembly, Guid loadedModule)
    {
        // The same read handle remains open for both metadata and hashing. Its
        // sharing mode prevents replacing this disk image during either read.
        using var pe = new PEReader(diskAssembly, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        Guid diskModule = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (diskModule == Guid.Empty || diskModule != loadedModule)
        { throw new IOException("Loaded Capture assembly does not match the on-disk module."); }
    }
    internal void VerifyPeer(Process peer)
    {
        if (peer.HasExited || !string.Equals(peer.MainModule?.FileName, AppHost, StringComparison.OrdinalIgnoreCase)
            || Read() != this)
        { throw new IOException("Peer apphost/build rejected."); }
    }
    internal static uint ClientPid(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) { throw new IOException("Missing native client PID."); }
        return pid;
    }
    internal static Process VerifyParent(NamedPipeClientStream pipe, PadProcessBuild build)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server)
            || !WindowsControllerProcessParents.Read().TryGetValue(Environment.ProcessId, out int parent)
            || server != parent)
        { throw new IOException("Pipe server is not the actual OS parent."); }
        var peer = Process.GetProcessById(parent);
        try
        {
            build.VerifyPeer(peer);
            if (peer.StartTime.ToUniversalTime() >= Process.GetCurrentProcess().StartTime.ToUniversalTime())
            { throw new IOException("Parent process lifetime rejected."); }
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

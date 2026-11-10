#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Deployment;

namespace OpenBlade.Setup;

internal sealed class WindowsScratchCandidateReadiness : IDisposable
{
    internal const string ImageName = "openblade-scratch-candidate-receipt.exe";
    private const string PipePrefix = "OpenBlade.Rust.CandidateReceipt.Test.";
    private readonly Process child;
    private readonly WindowsSetupRetainedCandidateProcess generation;
    private readonly NamedPipeClientStream pipe;
    private readonly string commitment;
    private readonly IDisposable location;
    private readonly FileStream image;

    private WindowsScratchCandidateReadiness(Process child, WindowsSetupRetainedCandidateProcess generation,
        NamedPipeClientStream pipe, byte[] commitment, IDisposable location, FileStream image)
    {
        this.child = child;
        this.generation = generation;
        this.pipe = pipe;
        this.commitment = Convert.ToHexStringLower(commitment);
        this.location = location;
        this.image = image;
    }

    internal static async Task<WindowsScratchCandidateReadiness> ConnectAsync(
        WindowsCandidateReceiptStorage storage, Guid scratchId, byte[] commitment, CancellationToken cancellationToken)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Scratch readiness requires a standard Windows token.");
        }
        string directory = Path.Combine(storage.Root, "receiver");
        string image = Path.Combine(directory, ImageName);
        var location = storage.PinDirectory(directory);
        FileStream? file = null;
        Process? child = null;
        WindowsSetupRetainedCandidateProcess? generation = null;
        NamedPipeClientStream? pipe = null;
        try
        {
            file = storage.OpenFile(image);
            string? expectedDigest = typeof(WindowsScratchCandidateReadiness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "OpenBladeScratchCandidateReadinessSha256")?.Value;
            if (expectedDigest is null
                || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The scratch readiness image differs from the fixture build pin.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string endpoint = Guid.NewGuid().ToString("D");
            child = Process.Start(new ProcessStartInfo(image)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            }) ?? throw new InvalidDataException("Could not launch the scratch readiness fixture.");
            var expected = new SetupCandidateProcessIdentity((uint)child.Id, child.StartTime.ToFileTimeUtc(), image);
            generation = WindowsSetupRetainedCandidateProcess.TryCapture(child.SafeHandle, expected, cancellationToken)
                ?? throw new InvalidDataException("Could not retain the launched scratch readiness generation.");
            byte[] bootstrap = [.. Encoding.ASCII.GetBytes(scratchId.ToString("D")), .. commitment,
                .. Encoding.ASCII.GetBytes(endpoint)];
            await child.StandardInput.BaseStream.WriteAsync(bootstrap, cancellationToken);
            child.StandardInput.Close();

            pipe = new NamedPipeClientStream(".", PipePrefix + endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            Task connection = pipe.ConnectAsync(connect.Token);
            Task exit = child.WaitForExitAsync(connect.Token);
            if (await Task.WhenAny(connection, exit) == exit)
            {
                connect.Token.ThrowIfCancellationRequested();
                await connect.CancelAsync();
                try { await connection; }
                catch (OperationCanceledException) { }
                throw new InvalidDataException("The scratch readiness fixture exited before connecting.");
            }
            await connection;
            await connect.CancelAsync();
            var readiness = new WindowsScratchCandidateReadiness(child, generation, pipe, commitment, location, file);
            readiness.RequireLive(cancellationToken);
            return readiness;
        }
        catch
        {
            pipe?.Dispose();
            generation?.Dispose();
            file?.Dispose();
            location.Dispose();
            if (child is not null)
            {
                StopChild(child);
            }
            throw;
        }
    }

    internal async Task VerifyAsync(int step, CancellationToken cancellationToken)
    {
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exchange.CancelAfter(TimeSpan.FromSeconds(2));
        RequireLive(exchange.Token);
        var request = new StartupFrame("verifyProvisional", OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), step, commitment);
        await IpcMessageCodec.WriteFramedAsync(pipe, request, exchange.Token);
        await ReadResponseAsync(pipe, request, exchange.Token);
        RequireLive(exchange.Token);
    }

    internal static async Task ReadResponseAsync(Stream stream, StartupFrame request, CancellationToken cancellationToken)
    {
        var response = await IpcMessageCodec.ReadFramedAsync<StartupFrame>(stream, cancellationToken);
        if (response != request with { Frame = "provisionalReady" })
        {
            throw new InvalidDataException("Scratch readiness response does not match the current startup request.");
        }
    }

    internal void RequireLive(CancellationToken cancellationToken)
    {
        if (!PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
            || SetupServiceHealthTiming.ReadPeerProcessId(pipe) is not uint peer
            || generation.ReadLive(cancellationToken) is not { } live || peer != live.ProcessId)
        {
            throw new InvalidDataException("The scratch readiness peer differs from the retained launch generation.");
        }
    }

    public void Dispose()
    {
        pipe.Dispose();
        generation.Dispose();
        try
        {
            StopChild(child);
        }
        finally
        {
            image.Dispose();
            location.Dispose();
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize,
        IntPtr bytesRead, IntPtr totalBytesAvailable, IntPtr bytesLeftThisMessage);

    private static void StopChild(Process child)
    {
        using (child)
        {
            if (!child.WaitForExit(1000))
            {
                try
                {
                    child.Kill();
                }
                catch (InvalidOperationException) when (child.HasExited)
                {
                    // It closed after the wait expired.
                }
                child.WaitForExit(1000);
            }
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record StartupFrame(
        [property: JsonRequired] string Frame,
        [property: JsonRequired] int Version,
        [property: JsonRequired] Guid RequestId,
        [property: JsonRequired] int Step,
        [property: JsonRequired] string Commitment);
}
#endif

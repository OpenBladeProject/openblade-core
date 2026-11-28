#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup;

internal sealed class WindowsScratchCandidateReadiness : IDisposable
{
    internal const string ImageName = ScratchNativeReceiverLaunch.ReadinessName;
    internal const byte PipeClaimedMarker = (byte)'1';
    private const string PipePrefix = "OpenBlade.Rust.CandidateReceipt.Test.";
    private readonly ScratchNativeReceiverLaunch launch;
    private readonly ScratchDiagnosticDrain diagnostics;
    private readonly WindowsSetupRetainedCandidateProcess generation;
    private readonly NamedPipeClientStream pipe;
    private readonly Guid deploymentRequestId;
    private readonly string commitment;
    private readonly IDisposable location;
    private readonly FileStream image;

    private WindowsScratchCandidateReadiness(ScratchNativeReceiverLaunch launch, WindowsSetupRetainedCandidateProcess generation,
        NamedPipeClientStream pipe, Guid deploymentRequestId, byte[] commitment, IDisposable location, FileStream image,
        ScratchDiagnosticDrain diagnostics)
    {
        this.launch = launch;
        this.diagnostics = diagnostics;
        this.generation = generation;
        this.pipe = pipe;
        this.deploymentRequestId = deploymentRequestId;
        this.commitment = Convert.ToHexStringLower(commitment);
        this.location = location;
        this.image = image;
    }

    internal static async Task<WindowsScratchCandidateReadiness> ConnectAsync(
        WindowsCandidateReceiptStorage storage, Guid deploymentRequestId, byte[] commitment, byte[] launchSecret,
        CancellationToken cancellationToken)
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
        ScratchNativeReceiverLaunch? launch = null;
        ScratchDiagnosticDrain? diagnostics = null;
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
            launch = ScratchNativeReceiverLaunch.StartReadiness(storage, file);
            diagnostics = new ScratchDiagnosticDrain(launch.StandardError);
            Process child = launch.Process;
            var expected = new SetupCandidateProcessIdentity((uint)child.Id, child.StartTime.ToFileTimeUtc(), image);
            generation = WindowsSetupRetainedCandidateProcess.TryCapture(child.SafeHandle, expected, cancellationToken)
                ?? throw new InvalidDataException("Could not retain the launched scratch readiness generation.");
            byte[] route = [.. Encoding.ASCII.GetBytes(storage.ScratchId.ToString("D")),
                .. Encoding.ASCII.GetBytes(endpoint)];
            await launch.StandardInput.BaseStream.WriteAsync(route, cancellationToken);
            await launch.StandardInput.BaseStream.WriteAsync(launchSecret, cancellationToken);
            launch.StandardInput.Close();

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            // Connecting earlier can take the instance Rust reserves for its parked client.
            byte[] marker = new byte[1];
            await launch.StandardOutput.BaseStream.ReadExactlyAsync(marker, connect.Token)
                .AsTask().WaitAsync(connect.Token);
            if (marker[0] != PipeClaimedMarker)
            {
                throw new InvalidDataException("The scratch readiness fixture did not claim its pipe.");
            }
            pipe = new NamedPipeClientStream(".", PipePrefix + endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
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
            var readiness = new WindowsScratchCandidateReadiness(launch, generation, pipe, deploymentRequestId,
                commitment, location, file, diagnostics);
            readiness.RequireLive(cancellationToken);
            return readiness;
        }
        catch (Exception failure)
        {
            ScratchFixtureFailure.Cleanup(failure,
                () => ScratchReceiverClosure.DisposeRetainedResources(new IDisposable?[]
                    { pipe, generation }.OfType<IDisposable>()),
                () => launch?.Dispose(() => diagnostics?.Finish(Console.Error)),
                () => ScratchReceiverClosure.DisposeRetainedResources(new IDisposable?[]
                    { file, location }.OfType<IDisposable>()));
            throw;
        }
    }

    internal async Task VerifyAsync(int step, CancellationToken cancellationToken)
    {
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The first reply now waits for issuer and disk verification after connect.
        exchange.CancelAfter(TimeSpan.FromSeconds(step == 1 ? 10 : 2));
        RequireLive(exchange.Token);
        var request = new StartupFrame("verifyProvisional", OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
            deploymentRequestId, step, commitment);
        await WriteFrameAsync(pipe, request, exchange.Token);
        await ReadResponseAsync(pipe, request, exchange.Token);
        RequireLive(exchange.Token);
    }

    internal static async Task ReadResponseAsync(Stream stream, StartupFrame request, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Scratch readiness response has duplicate members.");
                }
            }
            var response = JsonSerializer.Deserialize(payload, ScratchStartupJsonContext.Default.StartupFrame);
            if (response != request with { Frame = "provisionalReady" })
            {
                throw new InvalidDataException("Scratch readiness response does not match the current startup request.");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Scratch readiness response has an invalid body.", error);
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, StartupFrame frame, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, ScratchStartupJsonContext.Default.StartupFrame);
        if (payload.Length > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the 64 KiB limit.");
        }
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
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

    public void Dispose() => Dispose(null);

    internal void Dispose(Exception? failure) => ScratchFixtureFailure.Cleanup(failure,
        () => ScratchReceiverClosure.DisposeRetainedResources([pipe, generation]),
        () => launch.Dispose(() => diagnostics.Finish(Console.Error)),
        () => ScratchReceiverClosure.DisposeRetainedResources([image, location]));

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize,
        IntPtr bytesRead, IntPtr totalBytesAvailable, IntPtr bytesLeftThisMessage);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record StartupFrame(
        [property: JsonRequired] string Frame,
        [property: JsonRequired] int Version,
        [property: JsonRequired] Guid RequestId,
        [property: JsonRequired] Guid DeploymentRequestId,
        [property: JsonRequired] int Step,
        [property: JsonRequired] string Commitment);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WindowsScratchCandidateReadiness.StartupFrame))]
internal partial class ScratchStartupJsonContext : JsonSerializerContext;
#endif

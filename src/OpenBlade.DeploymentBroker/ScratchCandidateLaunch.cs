#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// Direct scratch launch only. Neither a Burn root PID nor a bundle owner GUID
// can issue this evidence on behalf of a descendant.
internal static class ScratchCandidateLaunch
{
    internal const string EvidenceName = "candidate-setup-launch.v3";
    internal const string ReceiverName = "OpenBlade.CandidateReceipt.Scratch.exe";
    internal const int SecretLength = 32;
    private const int ProcessIdentityLength = sizeof(int) + sizeof(long);

    internal static byte[] ReceiverIdentity(byte[] launchEvidence) =>
        launchEvidence[ProcessIdentityLength..(2 * ProcessIdentityLength)];

    internal static string ReceiverPath(WindowsCandidateReceiptStorage storage) =>
        Path.Combine(storage.Root, "receiver", ReceiverName);

    // The fixture passes the Process returned by its own direct launch and the
    // complete app-local closure held since before that launch. The child is
    // waiting on stdin.
    internal static void Bind(WindowsCandidateReceiptStorage storage, Guid request, Process child,
        ScratchReceiverClosure heldClosure, byte[] secret)
    {
        var paths = BrokerPaths.CreateForTests(storage.Root);
        using var root = storage.PinDirectory(storage.Root);
        string stage = Path.Combine(paths.StagingDirectory, request.ToString("N"));
        using var directory = storage.PinDirectory(stage);
        using var broker = Process.GetCurrentProcess();
        string image = ReceiverPath(storage);
        using var fixedClosure = ScratchReceiverClosure.Retain(storage);
        if (!CryptographicOperations.FixedTimeEquals(heldClosure.RequireUnchanged(), fixedClosure.RequireUnchanged()))
        {
            throw new InvalidDataException("The scratch launcher did not retain its fixed receiver closure.");
        }
        RequireLiveImage(child, image);
        ScratchNativeReceiverLaunch.RequirePolicies(child.SafeHandle);
        var store = new BrokerInFlightStore(paths);
        store.BindInstallerProcess(request, child.Id, DateTimeOffset.UtcNow);
        using var binding = storage.OpenFile(paths.InFlightPath);
        byte[] active = WindowsCandidateReceiptStorage.ReadBounded(binding, BrokerInFlightDeploymentCodec.MaximumBytes);
        var current = BrokerInFlightDeploymentCodec.ParseCanonical(active);
        if (current.RequestId != request || current.BrokerProcessId != broker.Id
            || current.InstallerProcessId != child.Id || current.InstallerExitCode.HasValue)
        {
            throw new InvalidDataException("The scratch launcher has no active launch binding for this request.");
        }
        storage.Publish(Path.Combine(stage, EvidenceName), Encode(active, broker, child, heldClosure, secret));
    }

    internal static byte[] Encode(byte[] active, Process broker, Process receiver, ScratchReceiverClosure closure, byte[] secret)
    {
        if (secret.Length != SecretLength || broker.HasExited || receiver.HasExited)
        {
            throw new InvalidDataException("The scratch launch requires live broker and receiver generations.");
        }

        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(broker.Id);
        writer.Write(CreationTime(broker));
        writer.Write(receiver.Id);
        writer.Write(CreationTime(receiver));
        writer.Write(SHA256.HashData(active));
        writer.Write(closure.RequireUnchanged());
        writer.Write(SHA256.HashData(secret));
        return bytes.ToArray();
    }

    private static long CreationTime(Process process)
    {
        if (!GetProcessTimes(process.SafeHandle, out long created, out _, out _, out _))
        {
            throw new InvalidDataException("The scratch process creation time is unavailable.");
        }
        return created;
    }

    internal static Process OpenBroker(int id)
    {
        try
        {
            var process = Process.GetProcessById(id);
            try
            {
                // Retain the OS process object for the entire receive operation.
                _ = process.SafeHandle;
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new InvalidDataException("The scratch broker generation could not be retained.", error);
        }
    }

    internal static void RequireLiveImage(Process process, string expected)
    {
        var name = new StringBuilder(512);
        int length = name.Capacity;
        if (process.HasExited || !QueryFullProcessImageNameW(process.SafeHandle, 0, name, ref length)
            || !string.Equals(name.ToString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The live scratch receiver image differs from its fixed launch image.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process, int flags, StringBuilder name, ref int length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
#endif

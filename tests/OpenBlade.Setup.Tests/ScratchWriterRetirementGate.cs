using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup.Tests;

// This gate is only used by the signed scratch rehearsal. Disk records are
// evidence to retain, never authority to reconstruct a lost process observer.
internal sealed class ScratchWriterRetirementGate(string directory)
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    internal enum Backend : byte { Managed = 1, Candidate = 2 }

    internal sealed record Generation(Backend Backend, Guid Instance, long Boot, long Creation,
        string ImagePath, string FixtureSha256, string BuildSha256);

    internal sealed record Observer(Generation Generation, SafeProcessHandle OriginalHandle);

    internal void ClaimFreshObserver()
    {
        if (Directory.EnumerateFileSystemEntries(directory, "scratch-retirement-*").Any())
        {
            throw new InvalidOperationException("Scratch retirement evidence requires the original observer.");
        }
        // Claim before launching either fixture. A new observer cannot
        // reconstruct authority even if the first one vanished before STOP.
        using var claim = new FileStream(Path.Combine(directory, "scratch-retirement-observer"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        claim.WriteByte(1);
        claim.Flush(flushToDisk: true);
    }

    internal void RequireRetired(Generation outgoing, bool scmStopped, Observer? observer,
        string? observedReceipt, Func<long> readBoot, Action requireRecoveryClear)
    {
        if (!scmStopped)
        {
            throw new InvalidOperationException("Writer stop was not confirmed.");
        }
        if (observer is null || observer.Generation != outgoing)
        {
            throw new InvalidOperationException("Scratch retirement evidence requires the original observer.");
        }

        SafeProcessHandle handle = observer.OriginalHandle;
        if (handle.IsClosed || handle.IsInvalid || WaitForSingleObject(handle, 0) != WaitObject0
            || !GetExitCodeProcess(handle, out uint exitCode) || exitCode != 0
            || !GetProcessTimes(handle, out long creation, out _, out _, out _) || creation != outgoing.Creation)
        {
            throw new InvalidOperationException("Outgoing writer process exit was not confirmed.");
        }
        if (readBoot() != outgoing.Boot)
        {
            throw new InvalidOperationException("Scratch retirement boot identity changed.");
        }
        if (!IsReceiptFor(outgoing, observedReceipt) || !Matches(outgoing, "intent")
            || !Matches(outgoing, "receipt", observedReceipt))
        {
            throw new InvalidOperationException("Retirement evidence is missing, incomplete, or stale.");
        }
        requireRecoveryClear();
    }

    internal static bool IsReceiptFor(Generation generation, string? frame)
    {
        string role = generation.Backend == Backend.Managed ? "managed" : "candidate";
        string expected = $"retired:{role}:{generation.Instance:N}";
        return frame == expected + "\n" || frame == expected + "\r\n";
    }

    internal string RecordPath(Generation generation, string role) =>
        Path.Combine(directory, $"scratch-retirement-{generation.Instance:N}.{role}");

    internal void WriteRecord(Generation generation, string role, Generation reported, bool torn = false,
        string? receipt = null)
    {
        string path = RecordPath(generation, role);
        byte[] bytes = Encode(reported, role, receipt);
        using (var file = new FileStream(path + ".new", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(torn ? bytes.AsSpan(0, 8) : bytes);
            file.Flush(flushToDisk: true);
        }
        if (!torn)
        {
            File.Move(path + ".new", path);
        }
    }

    private bool Matches(Generation generation, string role, string? receipt = null)
    {
        string path = RecordPath(generation, role);
        try
        {
            _ = File.GetAttributes(path + ".new");
            return false;
        }
        catch (FileNotFoundException)
        {
            // No interrupted publication is present.
        }
        try
        {
            using var file = File.OpenRead(path);
            byte[] expected = Encode(generation, role, receipt);
            byte[] actual = new byte[expected.Length];
            file.ReadExactly(actual);
            return file.ReadByte() == -1 && actual.AsSpan().SequenceEqual(expected);
        }
        catch (Exception error) when (error is FileNotFoundException or EndOfStreamException)
        {
            return false;
        }
    }

    private static byte[] Encode(Generation generation, string role, string? receipt)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write("OpenBlade scratch retirement v2");
        writer.Write(role);
        writer.Write((byte)generation.Backend);
        writer.Write(generation.Instance.ToByteArray());
        writer.Write(generation.Boot);
        writer.Write(generation.Creation);
        writer.Write(generation.ImagePath);
        writer.Write(Convert.FromHexString(generation.FixtureSha256));
        writer.Write(Convert.FromHexString(generation.BuildSha256));
        if (receipt is not null)
        {
            writer.Write(receipt);
        }
        return bytes.ToArray();
    }

    internal static long ReadWindowsBoot()
    {
        // Same OS-reported boot timestamp used by the Windows provisioning
        // and driver supervisors. No wall-clock/uptime estimate or PID fallback.
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        query.Options.Timeout = TimeSpan.FromSeconds(2);
        using ManagementObjectCollection results = query.Get();
        long boot = 0;
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                if (boot != 0 || result["LastBootUpTime"] is not string value || string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("Windows boot identity is unavailable.");
                }
                boot = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToFileTimeUtc();
            }
        }
        return boot > 0 ? boot : throw new InvalidOperationException("Windows boot identity is unavailable.");
    }

    internal static Observer ObserveLaunch(Process child, Backend backend, long boot, string imagePath,
        string fixtureSha256, string buildSha256, Guid instance)
    {
        // Process.Start keeps CreateProcess's handle on Windows. Use that
        // handle for identity and the terminal observation, with no reopen.
        SafeProcessHandle handle = child.SafeHandle;
        var path = new StringBuilder(32_768);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)
            || !string.Equals(imagePath, path.ToString(), StringComparison.OrdinalIgnoreCase)
            || !GetProcessTimes(handle, out long creation, out _, out _, out _) || creation <= boot
            || WaitForSingleObject(handle, 0) != WaitTimeout)
        {
            throw new InvalidOperationException("Scratch writer process identity is unavailable.");
        }

        return new(new(backend, instance, boot, creation, imagePath, fixtureSha256, buildSha256), handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder path, ref int size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}

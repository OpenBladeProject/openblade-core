using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Used only by the voltage-validation provider, for submissions. Fixed protected
// records and retained process handles bind the host's one monotonic budget to
// the actual owner/Service/driver lifetime. Reads never enter this path.
internal sealed class WindowsCpuVoltageWriteDeadline : IDisposable
{
    private const string DriverKey = @"SYSTEM\CurrentControlSet\Services\" + Rz090528InstalledCpuBackendInspector.ServiceName;
    private readonly List<IDisposable> pins = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal static CpuLiveCurveWriteDeadline Open(CancellationToken token)
    {
        var retained = new WindowsCpuVoltageWriteDeadline();
        try
        {
            token.ThrowIfCancellationRequested();
            string tag = ReadTag();
            string[] parts = tag.Split(':');
            if (parts.Length != 5 || !Guid.TryParseExact(parts[1], "N", out var run) || run == Guid.Empty)
            { throw new IOException("Validation driver owner is unavailable."); }
            retained.PinDirectory(CpuVoltageJobDeadline.Root);
            var authority = retained.Read<CpuVoltageRunDeadline>(Path.Combine(CpuVoltageJobDeadline.Root, "run-deadline-" + run.ToString("N") + ".json"));
            using var self = Process.GetCurrentProcess();
            if (!GetProcessTimes(self.SafeHandle, out long created, out _, out _, out _)
                || !authority.Matches(tag, Environment.ProcessId, created, OpenBladeProductVersion.Read(typeof(WindowsCpuVoltageWriteDeadline).Assembly))
                || authority.Run != run || !authority.Job.IsCurrent(Environment.TickCount64))
            { throw new IOException("Validation write lifetime differs or has expired."); }
            var job = authority.Job;
            string directory = Path.Combine(CpuVoltageJobDeadline.Root, job.Session.ToString("N"));
            retained.PinDirectory(directory);
            if (retained.Read<CpuVoltageJobDeadline>(Path.Combine(directory, job.FileName)) != job)
            { throw new IOException("Validation host deadline differs."); }
            var host = retained.PinProcess(job.HostPid, job.HostCreated, job.HostImage);
            var owner = retained.PinProcess(authority.OwnerPid, authority.OwnerCreated, authority.OwnerImage);
            var result = new CpuLiveCurveWriteDeadline(job.StartedTick, job.ExpiresTick, () => Environment.TickCount64,
                () => WaitForSingleObject(host, 0) == 258 && WaitForSingleObject(owner, 0) == 258 && ReadTag() == tag,
                token, retained);
            if (!result.IsCurrent()) { result.Dispose(); throw new IOException("Validation write deadline revoked."); }
            return result;
        }
        catch { retained.Dispose(); throw; }
    }

    private static string ReadTag()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(DriverKey, writable: false);
        return key is not null && key.GetValueKind("OpenBladePmOwner") == RegistryValueKind.String
            && key.GetValue("OpenBladePmOwner", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            && value.Length <= 256 ? value : throw new IOException("Validation driver owner is unavailable.");
    }

    private void PinDirectory(string path) => pins.Add(OpenProtected(path, directory: true));
    private T Read<T>(string path)
    {
        var handle = OpenProtected(path, directory: false);
        FileStream stream;
        try { stream = new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
        pins.Add(stream);
        if (stream.Length is <= 0 or > 8192) { throw new IOException("Validation deadline record size differs."); }
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new IOException("Missing validation deadline record.");
    }
    private static SafeFileHandle OpenProtected(string path, bool directory)
    {
        // Read sharing only retains the exact file/directory against replacement.
        var handle = CreateFileW(path, 0x80020000, 1, 0, 3, 0x00200000u | (directory ? 0x02000000u : 0), 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Protected validation record cannot be retained."); }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)
                || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            { throw new IOException("Validation record path is redirected or has the wrong type."); }
            uint error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out nint descriptor);
            if (error != 0) { throw new IOException("Validation record security unavailable."); }
            try
            {
                byte[] bytes = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                RequireProtection(new RawSecurityDescriptor(bytes, 0));
            }
            finally { LocalFree(descriptor); }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void RequireProtection(RawSecurityDescriptor descriptor)
    {
        static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544";
        if (!Trusted(descriptor.Owner?.Value) || descriptor.DiscretionaryAcl is null)
        { throw new IOException("Validation record owner or DACL is untrusted."); }
        const int writes = (int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x10000000 | 0x40000000;
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            { throw new IOException("Unsupported validation record ACE."); }
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && (ace.AccessMask & writes) != 0 && !Trusted(ace.SecurityIdentifier.Value))
            { throw new IOException("Validation record grants untrusted mutation."); }
        }
    }
    private SafeProcessHandle PinProcess(int pid, long created, string image)
    {
        var handle = OpenProcess(0x00101000, false, pid);
        try
        {
            var actualImage = new StringBuilder(1024); int length = actualImage.Capacity;
            if (handle.IsInvalid || !GetProcessTimes(handle, out long actualCreated, out _, out _, out _)
                || actualCreated != created || WaitForSingleObject(handle, 0) != 258
                || !QueryFullProcessImageNameW(handle, 0, actualImage, ref length) || actualImage.ToString() != image)
            { throw new IOException("Validation process identity differs."); }
            pins.Add(handle); return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public void Dispose()
    {
        for (int i = pins.Count - 1; i >= 0; i--) { pins[i].Dispose(); }
        pins.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int type, out AttributeTag info, uint size);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint requested, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref int length);
}

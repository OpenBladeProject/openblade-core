using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment;

public sealed record CpuVoltageHostEnrollment(Guid Session, string UserSid, int WindowsSession,
    int HostPid, long HostCreated, string HostImage, string Source, string Manifest, string ServiceVersion);

// The Capture host owns these fixed records. Readers never infer closure from an absent PID.
[SupportedOSPlatform("windows")]
public static class CpuVoltageHostState
{
    public const string Root = @"C:\ProgramData\OpenBladeCpuVoltageSessions";
    public static string PipeName(Guid session) => "OpenBlade.CpuVoltageSession." + session.ToString("N");

    public static CpuVoltageHostEnrollment? ReadActive()
    {
        if (!Directory.Exists(Root)) { return null; }
        RequireProtected(Root, true);
        string marker = Path.Combine(Root, "active-session.json");
        if (!File.Exists(marker)) { return null; }
        Guid session = JsonSerializer.Deserialize<Guid>(ReadProtected(marker));
        if (session == Guid.Empty) { throw new InvalidDataException("Invalid active CPU host identity."); }
        var value = JsonSerializer.Deserialize<CpuVoltageHostEnrollment>(
            ReadProtected(Path.Combine(Root, session.ToString("N"), "enrollment.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Missing CPU host enrollment.");
        if (value.Session != session || value.HostPid <= 0 || value.HostCreated <= 0
            || value.WindowsSession <= 0 || string.IsNullOrWhiteSpace(value.UserSid)
            || !Path.IsPathFullyQualified(value.HostImage))
        { throw new InvalidDataException("Malformed CPU host enrollment."); }
        return value;
    }

    public static void RequireClosedAndTerminal(CpuVoltageHostEnrollment enrollment)
    {
        using JsonDocument closed = JsonDocument.Parse(ReadProtected(
            Path.Combine(Root, enrollment.Session.ToString("N"), "closed.json")));
        if (closed.RootElement.GetProperty("session").GetGuid() != enrollment.Session
            || !closed.RootElement.GetProperty("idleResolved").GetBoolean())
        { throw new InvalidDataException("The CPU host has no matching resolved closure."); }
        if (IsExactProcessRunning(enrollment))
        { throw new IOException("The previous CPU host is still running."); }
    }

    public static bool IsExactProcessRunning(CpuVoltageHostEnrollment enrollment)
    {
        using SafeProcessHandle process = OpenProcess(0x1000 | 0x100000, false, enrollment.HostPid);
        if (process.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 87) { return false; }
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!GetProcessTimes(process, out long created, out _, out _, out _))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (created != enrollment.HostCreated) { return false; }
        StringBuilder image = new(32768); int length = image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref length))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!string.Equals(image.ToString(), enrollment.HostImage, StringComparison.OrdinalIgnoreCase)
            || !ProcessIdToSessionId(enrollment.HostPid, out int session)
            || session != enrollment.WindowsSession)
        { throw new IOException("The CPU host process identity changed."); }
        uint wait = WaitForSingleObject(process, 0);
        return wait == 258 ? true : wait == 0 ? false
            : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static byte[] ReadProtected(string path)
    {
        RequireProtected(Path.GetDirectoryName(path)!, true);
        RequireProtected(path, false);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 16384) { throw new InvalidDataException("CPU host record size rejected."); }
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    public static void RequireProtected(string path, bool directory)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Redirected CPU host path."); }
        }
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        RawSecurityDescriptor security = new(acl.GetSecurityDescriptorBinaryForm(), 0);
        static bool Trusted(SecurityIdentifier? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544"
            or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        const int writes = (int)(FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership) | 0x10000000 | 0x40000000;
        if (!Trusted(security.Owner) || security.DiscretionaryAcl is null)
        { throw new IOException("Unprotected CPU host owner or DACL."); }
        foreach (GenericAce entry in security.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback
                || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            { throw new IOException("Unsupported CPU host protection ACE."); }
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && (ace.AccessMask & writes) != 0 && !Trusted(ace.SecurityIdentifier))
            { throw new IOException("CPU host object grants untrusted mutation rights."); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

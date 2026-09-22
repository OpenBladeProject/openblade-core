using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

// The desktop shell is selected only through Windows' shell window. A caller
// cannot supply a parent PID, executable, token, or command line.
internal sealed class WindowsDesktopLaunchContext : IDeploymentLaunchContext
{
    private readonly SafeProcessHandle parent;
    private readonly uint parentId;
    private readonly DesktopProcessIdentity identity;

    private WindowsDesktopLaunchContext(SafeProcessHandle parent, uint parentId,
        DesktopProcessIdentity identity)
    {
        this.parent = parent;
        this.parentId = parentId;
        this.identity = identity;
    }

    internal static WindowsDesktopLaunchContext Prepare()
    {
        uint shellId = GetCurrentShellId();
        SafeProcessHandle parent = OpenProcess(0x00101080, false, shellId);
        if (parent.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            parent.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            DesktopProcessIdentity caller = DesktopProcessIdentity.ReadCaller();
            WindowsDesktopLaunchContext context = new(parent, shellId, caller);
            context.Validate();
            return context;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    public void Validate()
    {
        if (parent.IsClosed || parent.IsInvalid
            || IndependentInstalledProcess.HasExited(parent)
            || GetCurrentShellId() != parentId
            || !new WindowsClientSessionSource().IsActiveLocalConsoleSession())
        {
            throw new UnauthorizedAccessException("The verified desktop shell is no longer available.");
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string expected = Path.Combine(windows, "explorer.exe");
        StringBuilder image = new(32768);
        uint length = (uint)image.Capacity;
        if (!QueryFullProcessImageNameW(parent, 0, image, ref length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!Path.IsPathFullyQualified(windows)
            || !string.Equals(image.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(windows) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0
            || WindowsInstalledPayloadVerifier.IsCallerWritable(new DirectoryInfo(windows))
            || WindowsInstalledPayloadVerifier.IsCallerWritable(new FileInfo(expected)))
        {
            throw new UnauthorizedAccessException("The desktop shell executable is not the protected Windows Explorer.");
        }

        DesktopProcessIdentity caller = DesktopProcessIdentity.ReadCaller();
        DesktopProcessIdentity currentParent = DesktopProcessIdentity.Read(parent);
        if (!identity.IsSameNormalIdentity(caller)
            || !identity.IsSameNormalIdentity(currentParent))
        {
            throw new UnauthorizedAccessException("The desktop shell and deployment caller security contexts differ.");
        }
    }

    public ILaunchedDeploymentProcess Launch(InstalledProcessLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InstalledPayloadPaths? expected = InstalledPayloadPathPolicy.GetExpectedPaths();
        if (expected is null || !InstalledProcessLaunchPolicy.IsAllowed(plan, expected))
        {
            throw new UnauthorizedAccessException("The deployment client refused a non-fixed installed process launch.");
        }

        Validate();
        return new WindowsLaunchedDeploymentProcess(IndependentInstalledProcess.Start(plan, parent, identity));
    }

    public void Dispose() => parent.Dispose();

    private static uint GetCurrentShellId()
    {
        IntPtr window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0)
        {
            throw new UnauthorizedAccessException("The active desktop shell is unavailable.");
        }

        return processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}

internal sealed record DesktopProcessIdentity(string UserSid, long AuthenticationId, int SessionId,
    string IntegritySid, bool Elevated, bool Restricted, bool AppContainer,
    string[] Groups, string[] Privileges, bool UiAccess = false)
{
    internal bool IsNormal => !Elevated && !Restricted && !AppContainer && !UiAccess
        && IntegritySid == "S-1-16-8192";

    internal bool IsSameNormalIdentity(DesktopProcessIdentity other) => IsNormal && other.IsNormal
        && UserSid == other.UserSid && AuthenticationId == other.AuthenticationId
        && SessionId == other.SessionId && IntegritySid == other.IntegritySid
        && Groups.SequenceEqual(other.Groups) && Privileges.SequenceEqual(other.Privileges);

    internal static DesktopProcessIdentity ReadCaller()
    {
        using WindowsIdentity? impersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (impersonation is not null)
        {
            throw new UnauthorizedAccessException("An impersonating deployment caller cannot select a desktop parent.");
        }

        using Process process = Process.GetCurrentProcess();
        return Read(process.SafeHandle);
    }

    internal static DesktopProcessIdentity Read(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 0x0008, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            string user = ReadInformation(token, 1, buffer => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value);
            long authenticationId = ReadInformation(token, 10, buffer => Marshal.ReadInt64(buffer, 8));
            int sessionId = ReadInformation(token, 12, Marshal.ReadInt32);
            string integrity = ReadInformation(token, 25, buffer => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value);
            bool elevated = ReadInformation(token, 20, Marshal.ReadInt32) != 0;
            bool appContainer = ReadInformation(token, 29, Marshal.ReadInt32) != 0;
            bool uiAccess = ReadInformation(token, 26, Marshal.ReadInt32) != 0;
            string[] groups = ReadInformation(token, 2, buffer =>
            {
                int count = Marshal.ReadInt32(buffer);
                List<string> values = [];
                int stride = Marshal.SizeOf<SidAndAttributes>();
                for (int index = 0; index < count; index++)
                {
                    SidAndAttributes group = Marshal.PtrToStructure<SidAndAttributes>(buffer + IntPtr.Size + index * stride);
                    values.Add($"{new SecurityIdentifier(group.Sid).Value}:{group.Attributes:X8}");
                }

                return values.Order(StringComparer.Ordinal).ToArray();
            });
            string[] privileges = ReadInformation(token, 3, buffer =>
            {
                int count = Marshal.ReadInt32(buffer);
                List<string> values = [];
                for (int index = 0; index < count; index++)
                {
                    int offset = 4 + index * 12;
                    // Include disabled privileges too: a filtered caller must
                    // not regain a removed privilege through the desktop token.
                    values.Add($"{Marshal.ReadInt64(buffer, offset):X16}:{Marshal.ReadInt32(buffer, offset + 8) & 7:X8}");
                }

                return values.Order(StringComparer.Ordinal).ToArray();
            });
            return new(user, authenticationId, sessionId, integrity, elevated, IsTokenRestricted(token),
                appContainer, groups, privileges, uiAccess);
        }
    }

    private static T ReadInformation<T>(SafeAccessTokenHandle token, int informationClass, Func<IntPtr, T> read)
    {
        GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out int length);
        if (length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return read(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
}

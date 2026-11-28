using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Setup;
using OpenBlade.Tray;

namespace OpenBlade.VmFixture;

internal static class Program
{
    private const string Root = @"C:\OpenBladeRustPhase5jVm";
    private const string Data = Root + @"\data";
    private const string ServiceName = "OpenBladeRustPhase5jVm";
    private const string Image = Root + @"\OpenBladeRustPhase5jVm.exe";
    private const string PipeName = "OpenBlade.Rust.ReadOnly.Dev.phase5j-vm";
    private const string LeaseName = "configuration.writer-lock";
    private static readonly string[] Documents =
        ["profiles.json", "battery-preferences.json", "battery-configuration.commit.json"];
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["prepare"])
            {
                Require(new DriveInfo(@"C:\") is { DriveType: DriveType.Fixed, DriveFormat: "NTFS" },
                    "The fixture requires a fixed C: NTFS volume.");
                CreateProtectedDirectory(Root);
                CreateProtectedDirectory(Data);
                CreateProtectedDirectory(Path.Combine(Root, "client"));
            }

            VerifyDirectory(Root);
            VerifyDirectory(Data);
            switch (args)
            {
                case ["prepare"]:
                    Require(!Directory.EnumerateFileSystemEntries(Data).Any(), "Expected empty fixture data.");
                    byte[][] baseline = ReadOracle("prior");
                    for (int index = 0; index < Documents.Length; index++)
                    {
                        WriteProtected(Path.Combine(Data, Documents[index]), baseline[index]);
                    }
                    break;
                case ["stage", var source, var digest]:
                    var build = new FileInfo(source);
                    Require(Path.IsPathFullyQualified(source)
                        && Path.GetPathRoot(build.FullName) is { Length: 3 } drive
                        && new DriveInfo(drive).DriveType == DriveType.Fixed
                        && build.Name is "openblade-windows-scm-vm.exe" or "OpenBladeRustPhase5jVm.exe",
                        "Expected the dedicated VM binary on a local fixed drive.");
                    Require(build.Exists && !build.Attributes.HasFlag(FileAttributes.ReparsePoint),
                        "Expected a plain fixture build artifact.");
                    byte[] image = File.ReadAllBytes(build.FullName);
                    Require(Convert.ToHexString(SHA256.HashData(image)).Equals(digest, StringComparison.OrdinalIgnoreCase),
                        "The staged bytes do not match the reviewed image digest.");
                    WriteProtected(Image, image);
                    break;
                case ["seal-client"]:
                    string clientDirectory = Path.Combine(Root, "client");
                    Require(Directory.Exists(clientDirectory), "Expected a staged managed client directory.");
                    await new WindowsMachineDataDirectorySecurity().ProtectAsync(clientDirectory, CancellationToken.None);
                    break;
                case ["release-start"]:
                    CreateProtectedMarker("start.release");
                    break;
                case ["release-renderer"]:
                    CreateProtectedMarker("renderer.release");
                    break;
                case ["pipe-absent"]:
                    bool available = WaitNamedPipe(@"\\.\pipe\" + PipeName, 200);
                    int pipeError = Marshal.GetLastWin32Error();
                    Require(!available && pipeError == 2, $"Expected pipe absence (2), observed {pipeError}.");
                    break;
                case ["deny"]:
                    AssertDifferentAccountDenied();
                    break;
                case ["exercise", var pidText] when uint.TryParse(pidText, out uint pid) && pid != 0:
                    await ExerciseAsync(pid);
                    break;
                case ["verify"]:
                    using (FileStream lease = OpenLease())
                    {
                        byte[][] committed = ReadCommitted();
                        string expected = File.ReadAllText(Path.Combine(Root, "client.accepted"));
                        Require(expected == Describe(committed), "The durable bytes changed after the accepted readback.");
                        Require(File.ReadAllText(Path.Combine(Root, "client.drained")) == "lighting-response-closed\n",
                            "The managed client did not record the pending response closing.");
                    }
                    break;
                case ["lease-held"]:
                    AssertLeaseHeld();
                    break;
                default:
                    throw new ArgumentException("Expected prepare, stage <image> <SHA-256>, seal-client, release-start, release-renderer, pipe-absent, deny, exercise <SCM PID>, lease-held, or verify.");
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task ExerciseAsync(uint pid)
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        Require(current.IsSystem && ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)
            && session == 0, "Run the fixture client as LocalSystem in session 0.");
        using SafeProcessHandle server = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (server.IsInvalid || !OpenProcessToken(server, 0x0008, out SafeAccessTokenHandle tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening the SCM process token");
        }
        using (tokenHandle)
        using (var serverIdentity = new WindowsIdentity(tokenHandle.DangerousGetHandle()))
        {
            Require(serverIdentity.IsSystem, "The SCM process token is not LocalSystem.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        await using var client = new ServiceClient(ConnectAsync, TimeProvider.System);
        await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
        ServiceStatusSnapshot initial = await WaitForMessageAsync(watch, "Renderer frame in flight.");
        Require(initial.LightingRendererOwnership == LightingRendererOwnershipState.Spectrum
            && initial.Profiles.BatteryPolicy.ChargeLimitPercent == 80,
            "Unexpected initial renderer or saved battery policy.");
        byte[][] baseline = ReadOracle("prior");
        Require(Documents.Select((name, index) =>
            File.ReadAllBytes(Path.Combine(Data, name)).AsSpan().SequenceEqual(baseline[index])).All(equal => equal),
            "The service changed the seeded aggregate before the typed profile update.");

        var profiles = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
        var result = await client.UpdateProfilesAsync(profiles, token);
        Require(result.Status == ControlOperationStatus.Stored, "The typed profile edit did not return Stored.");
        var status = await client.GetStatusAsync(token);
        Require(status.Profiles.BatteryPolicy.ChargeLimitPercent == 75
            && status.ConfirmedState?.Battery == new ConfirmedBatteryState(true, 80, false),
            "The saved policy and independently reported scripted battery cap did not stay separate.");
        byte[][] committed = ReadCommitted();

        var off = new ManualOverride<LightingSettings>(LightingSettingsFactory.Off(), OverrideLifetime.Persistent);
        Task<ControlOperationResult> applying = client.SetLightingOverrideAsync(off, token);
        ServiceStatusSnapshot pending = await WaitForMessageAsync(watch, "Frame drained. Renderer port still owned.");
        Require(pending.LightingRendererOwnership == LightingRendererOwnershipState.Spectrum
            && pending.ConfirmedState?.Lighting is null && pending.WritesBlocked && !applying.IsCompleted,
            "The Off response must wait for the separately held renderer port.");
        WriteProtected(Path.Combine(Root, "client.accepted"), Encoding.UTF8.GetBytes(Describe(committed)));
        try
        {
            await applying;
        }
        catch (IOException error) when (!token.IsCancellationRequested && IsPipeClosure(error))
        {
            AssertStopPending();
            WriteProtected(Path.Combine(Root, "client.drained"), "lighting-response-closed\n"u8);
            return;
        }

        throw new InvalidOperationException("Expected SCM STOP to close the pending Off response.");

        async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(2000, cancellationToken);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualPid))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading the connected pipe server PID");
                }
                Require(actualPid == pid && WaitForSingleObject(server, 0) == 258,
                    "The pipe server is not the retained live SCM process.");
                var security = new RawSecurityDescriptor(pipe.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
                Require(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
                    && security.DiscretionaryAcl is { Count: 1 } acl
                    && acl[0] is CommonAce { AceQualifier: AceQualifier.AccessAllowed, AceFlags: AceFlags.None } ace
                    && ace.SecurityIdentifier.Equals(SystemSid)
                    && ace.AccessMask is 0x10000000 or 0x001F01FF,
                    "Expected exactly one protected LocalSystem full-access pipe ACE.");
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    private static bool IsPipeClosure(IOException error) =>
        error is EndOfStreamException
        || error.HResult is unchecked((int)0x8007006D) // ERROR_BROKEN_PIPE
            or unchecked((int)0x800700E8)             // ERROR_NO_DATA
            or unchecked((int)0x800700E9);            // ERROR_PIPE_NOT_CONNECTED

    private static void AssertStopPending()
    {
        IntPtr manager = OpenSCManager(IntPtr.Zero, IntPtr.Zero, 1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening the local SCM database");
        }

        try
        {
            IntPtr service = OpenService(manager, ServiceName, 4); // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening the fixed VM service");
            }

            try
            {
                if (!QueryServiceStatus(service, out ServiceStatus status))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading the fixed VM service status");
                }

                Require(status.CurrentState == 3, "The lighting response closed outside SCM STOP_PENDING.");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static async Task<ServiceStatusSnapshot> WaitForMessageAsync(
        IAsyncEnumerator<ServiceWatchUpdate> watch, string message)
    {
        while (await watch.MoveNextAsync())
        {
            if (watch.Current is ServiceWatchUpdate.Connected update && update.Status.Message == message)
            {
                return update.Status;
            }
        }

        throw new InvalidOperationException($"The subscription closed before '{message}'.");
    }

    private static void AssertDifferentAccountDenied()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Require(!identity.IsSystem, "Run the denied-open probe as the separate VM administrator.");
        using SafeFileHandle probe = CreateFile(@"\\.\pipe\" + PipeName, 0xC0000000,
            0, IntPtr.Zero, FileMode.Open, 0x00110000, IntPtr.Zero); // SECURITY_SQOS_PRESENT | IDENTIFICATION
        int error = Marshal.GetLastWin32Error();
        Require(probe.IsInvalid && error == 5, $"Expected Windows access denied (5), observed {error}.");
    }

    private static void AssertLeaseHeld()
    {
        try
        {
            using FileStream lease = OpenLease();
        }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070020))
        {
            return;
        }

        throw new InvalidOperationException("The protected configuration writer lease was released.");
    }

    private static FileStream OpenLease() =>
        new(Path.Combine(Data, LeaseName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static byte[][] ReadCommitted()
    {
        byte[][] expected = ReadOracle("committed");
        byte[][] actual = Documents.Select(name => File.ReadAllBytes(Path.Combine(Data, name))).ToArray();
        Require(JsonNode.DeepEquals(JsonNode.Parse(actual[0]), JsonNode.Parse(expected[0]))
            && actual[1].AsSpan().SequenceEqual(expected[1]),
            "The managed oracle differs from the committed profile or unchanged preferences.");
        using var commit = JsonDocument.Parse(actual[2]);
        using var oracleCommit = JsonDocument.Parse(expected[2]);
        JsonElement marker = commit.RootElement;
        Require(marker.GetProperty("schemaVersion").GetInt32() == oracleCommit.RootElement.GetProperty("schemaVersion").GetInt32()
            && marker.GetProperty("profileSha256").GetString() == Convert.ToHexString(SHA256.HashData(actual[0]))
            && marker.GetProperty("preferencesSha256").GetString() == Convert.ToHexString(SHA256.HashData(actual[1])),
            "The commit marker does not describe the actual aggregate.");
        return actual;
    }

    private static byte[][] ReadOracle(string version)
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("OpenBlade.VmFixture.Aggregate")!;
        using var fixture = JsonDocument.Parse(stream);
        return fixture.RootElement.GetProperty(version).EnumerateArray()
            .Select(document => Encoding.UTF8.GetBytes(document.GetString()!)).ToArray();
    }

    private static string Describe(byte[][] committed) =>
        string.Concat(committed.Select((bytes, index) =>
            $"{Documents[index]} {Convert.ToHexString(SHA256.HashData(bytes))}\n"));

    private static void CreateProtectedDirectory(string path)
    {
        byte[] descriptor = WindowsMachineDataDirectorySecurity.CreateProtectedSecurity().GetSecurityDescriptorBinaryForm();
        GCHandle allocation = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = allocation.AddrOfPinnedObject(),
            };
            if (!CreateDirectory(path, in security))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Creating {path}");
            }
        }
        finally
        {
            allocation.Free();
        }
    }

    private static void VerifyDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        Require(directory.Exists && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint),
            $"Expected a plain protected fixture directory at {path}.");
        WindowsMachineDataDirectorySecurity.VerifyProtectedSecurity(
            directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static void CreateProtectedMarker(string name)
    {
        byte[] descriptor = WindowsMachineDataDirectorySecurity.CreateProtectedFileSecurity().GetSecurityDescriptorBinaryForm();
        GCHandle allocation = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = allocation.AddrOfPinnedObject(),
            };
            // A read-open may race with creation. This empty signal needs no
            // data access or flush, so the service can open it immediately.
            using SafeFileHandle marker = CreateMarker(Path.Combine(Root, name), 0,
                FileShare.ReadWrite | FileShare.Delete, in security, FileMode.CreateNew, 0, IntPtr.Zero);
            if (marker.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Creating {name}");
            }
        }
        finally
        {
            allocation.Free();
        }
    }

    private static void WriteProtected(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.None, 4096, FileOptions.None, WindowsMachineDataDirectorySecurity.CreateProtectedFileSecurity());
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint Type;
        internal uint CurrentState;
        internal uint AcceptedControls;
        internal uint Win32ExitCode;
        internal uint ServiceExitCode;
        internal uint Checkpoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenSCManager(IntPtr machine, IntPtr database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string service, uint access);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string name, in SecurityAttributes security);

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateMarker(string name, uint access, FileShare share,
        in SecurityAttributes security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);
}

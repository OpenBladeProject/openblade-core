using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Platform;

namespace OpenBlade.SessionAgent;

internal sealed record ThxDriverApiNode(
    string Id,
    string Parent,
    IReadOnlyList<string> HardwareIds,
    uint Status,
    uint Problem,
    string StoreInf);

internal sealed record ThxDriverApiEvidence(
    bool Trusted,
    string Identity,
    string ServiceBinary,
    string? Error)
{
    internal static ThxDriverApiEvidence Rejected(string error) => new(false, string.Empty, string.Empty, error);
}

/// <summary>
/// Binds the THX service and APO APIs to present exact-codec SWC devnodes and
/// their Windows-trusted DriverStore catalogs. Package bytes and versions are
/// observations only; API compatibility is established by the isolated helper's
/// typed reads.
/// </summary>
internal static class WindowsThxDriverApiDiscovery
{
    private const int MaximumDevices = 8192;
    private static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DriverFormat = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid NodeFormat = new("4340a6c5-93fa-4706-972c-7b648008a5a7");

    internal static ThxDriverApiEvidence Capture(
        ThxAudioCompatibilityProfile profile,
        string? registeredLegacyApoPath = null)
    {
        try
        {
            if (!WindowsThxAdmissionQueries.TryGetRunningServiceBinary(
                profile.Stack.ServiceName, out string? serviceBinary))
            {
                return ThxDriverApiEvidence.Rejected(
                    $"THX service {profile.Stack.ServiceName} is not running.");
            }
            return Evaluate(profile, ReadInventory(), serviceBinary!, registeredLegacyApoPath,
                static (catalog, members) => WindowsDriverApiTrust.VerifyCatalogMembers(
                    catalog,
                    members,
                    [WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher]));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException
            or Win32Exception or CryptographicException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.WindowsThxDriverApiDiscovery",
                "Discover",
                exception);
            return ThxDriverApiEvidence.Rejected(
                $"The installed THX API packages could not be verified ({DescribeVerificationFailure(exception)}).");
        }
    }

    internal static ThxDriverApiEvidence Evaluate(
        ThxAudioCompatibilityProfile profile,
        IReadOnlyList<ThxDriverApiNode> nodes,
        string serviceBinary,
        string? registeredLegacyApoPath,
        Func<string, IReadOnlyList<string>, string> verifyCatalogMembers)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(verifyCatalogMembers);
        if (string.IsNullOrWhiteSpace(serviceBinary) || !Path.IsPathFullyQualified(serviceBinary)
            || !File.Exists(serviceBinary)
            || !Path.GetFileName(serviceBinary).Equals(profile.Stack.ServiceExecutable,
                StringComparison.OrdinalIgnoreCase))
        {
            return ThxDriverApiEvidence.Rejected(
                $"The running THX service does not use {profile.Stack.ServiceExecutable}.");
        }
        if (nodes.Count > MaximumDevices
            || nodes.Select(node => node.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodes.Count)
        {
            return ThxDriverApiEvidence.Rejected("The THX device inventory is oversized or ambiguous.");
        }

        ThxDriverApiNode[] codecs = nodes.Where(node => Healthy(node)
            && ThxAudioCompatibility.MatchesCodec(profile, node.HardwareIds)).ToArray();
        if (codecs.Length == 0)
        {
            return ThxDriverApiEvidence.Rejected("The exact THX codec binding is absent.");
        }

        RuntimePackageContract[] contracts = Contracts(profile);
        var graph = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        // The exact healthy codec devnode is the ancestry root for both THX
        // software components. Its Windows servicing package may use an inbox
        // catalog without a DriverStore sidecar; OpenBlade does not load its
        // binaries. Catalog membership below therefore covers the actual THX
        // service and APO packages that provide the accessed API.
        var identities = new List<string>(contracts.Length);
        foreach (RuntimePackageContract contract in contracts)
        {
            ThxDriverApiNode[] matches = nodes.Where(node => Healthy(node)
                && Path.GetFileName(node.StoreInf).Equals(contract.InfName, StringComparison.OrdinalIgnoreCase)
                && node.HardwareIds.Any(actual => contract.HardwareIds.Contains(actual, StringComparer.OrdinalIgnoreCase))
                && DescendsFromExactCodec(node, codecs, graph)).ToArray();
            string[] boundPackages = matches.Select(node => Path.GetFullPath(node.StoreInf))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (boundPackages.Length != 1)
            {
                return ThxDriverApiEvidence.Rejected(
                    $"The installed THX {contract.Role} package is missing, ambiguous, or not bound to the exact codec.");
            }

            string storeInf = boundPackages[0];
            string root = Path.GetDirectoryName(storeInf)
                ?? throw new InvalidDataException("The THX package root is unavailable.");
            var members = new List<string> { storeInf };
            if (contract.IsService)
            {
                members.Add(Path.GetFullPath(serviceBinary));
            }
            if (contract.IsApo && profile.Backend == ThxAudioNativeBackend.Legacy265)
            {
                if (string.IsNullOrWhiteSpace(registeredLegacyApoPath)
                    || !Path.IsPathFullyQualified(registeredLegacyApoPath)
                    || !File.Exists(registeredLegacyApoPath))
                {
                    return ThxDriverApiEvidence.Rejected("The registered legacy THX APO is unavailable.");
                }
                string[] allowedApos = profile.Stack.Packages.Single(package =>
                        package.InfName.Equals(contract.InfName, StringComparison.OrdinalIgnoreCase))
                    .Files.Where(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Name).ToArray();
                if (!allowedApos.Contains(Path.GetFileName(registeredLegacyApoPath),
                    StringComparer.OrdinalIgnoreCase))
                {
                    return ThxDriverApiEvidence.Rejected(
                        "The registered legacy THX APO does not match the admitted API contract.");
                }
                members.Add(Path.GetFullPath(registeredLegacyApoPath));
            }
            foreach (string memberName in contract.PackageMembers ?? [])
            {
                string member = Path.Combine(root, memberName);
                if (!File.Exists(member))
                {
                    return ThxDriverApiEvidence.Rejected(
                        $"The installed THX {contract.Role} API member {memberName} is missing.");
                }
                members.Add(member);
            }
            try
            {
                identities.Add(VerifyBoundPackage(contract.Role, storeInf, members, verifyCatalogMembers));
            }
            catch (Exception exception) when (IsTrustFailure(exception))
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.SessionAgent.WindowsThxDriverApiDiscovery",
                    "VerifyBoundPackage",
                    exception,
                    contract.Role);
                return ThxDriverApiEvidence.Rejected(
                    $"The installed THX {contract.Role} package {Path.GetFileName(storeInf)} failed "
                    + $"catalog trust ({DescribeVerificationFailure(exception)}).");
            }
        }

        // ProgramData tuning/configuration is unsigned API-managed data and is
        // deliberately excluded from runtime identity. Its compatibility is
        // established by the bounded typed reads in the isolated helper.
        string observation = string.Join('\n', identities.Append(Path.GetFullPath(serviceBinary).ToUpperInvariant()));
        return new(true,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation))),
            Path.GetFullPath(serviceBinary),
            null);
    }

    private static string VerifyBoundPackage(
        string role,
        string storeInf,
        IReadOnlyList<string> members,
        Func<string, IReadOnlyList<string>, string> verifyCatalogMembers)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(storeInf))
            ?? throw new InvalidDataException("The THX package root is unavailable.");
        RejectReparseChain(root);
        string[] catalogs = Directory.GetFiles(root, "*.cat", SearchOption.TopDirectoryOnly);
        if (catalogs.Length != 1)
        {
            throw new InvalidDataException($"The installed THX {role} catalog is missing or ambiguous.");
        }
        return verifyCatalogMembers(catalogs[0], members);
    }

    private static bool IsTrustFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
        or ArgumentException or Win32Exception or CryptographicException;

    private static string DescribeVerificationFailure(Exception exception) => exception switch
    {
        CryptographicException cryptographic => cryptographic.Message,
        Win32Exception win32 => $"Win32 error {win32.NativeErrorCode} (0x{win32.NativeErrorCode:X8})",
        _ => exception.GetType().Name,
    };

    private static RuntimePackageContract[] Contracts(ThxAudioCompatibilityProfile profile)
    {
        if (profile.Backend == ThxAudioNativeBackend.Legacy265)
        {
            return
            [
                new("service", "THXRTSVC.inf", [@"SWC\VEN_THX&PID_THXSVC"], IsService: true, IsApo: false),
                new("APO", "THXRTAPO.inf",
                    [@"SWC\VEN_THX&CID_THXAPO", @"SWC\VEN_THX&CID_THXAPO_SSE2"],
                    IsService: false, IsApo: true, PackageMembers: []),
            ];
        }
        string family = profile.ProductId.ToUpperInvariant();
        return
        [
            new("service", $"thx_{profile.ProductId}_svc.inf",
                [$@"SWC\VEN_THX&SID_{family}SVC"], IsService: true, IsApo: false,
                PackageMembers: []),
            new("APO", $"thx_{profile.ProductId}_apo4.inf",
                [$@"SWC\VEN_THX&AID_{family}APO4"], IsService: false, IsApo: true,
                PackageMembers: profile.Stack.Packages.Single(package =>
                        package.InfName.Equals($"thx_{profile.ProductId}_apo4.inf", StringComparison.OrdinalIgnoreCase))
                    .Files.Where(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || file.Name.EndsWith("_data.bin", StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Name).ToArray()),
        ];
    }

    private static bool Healthy(ThxDriverApiNode node) => node.Problem == 0 && (node.Status & 8) != 0
        && node.StoreInf.Length != 0;

    private static bool DescendsFromExactCodec(
        ThxDriverApiNode node,
        IReadOnlyList<ThxDriverApiNode> codecs,
        IReadOnlyDictionary<string, ThxDriverApiNode> graph)
    {
        string parent = node.Parent;
        for (int depth = 0; depth < 16 && parent.Length != 0; depth++)
        {
            if (codecs.Any(codec => codec.Id.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (!graph.TryGetValue(parent, out ThxDriverApiNode? ancestor)
                || ancestor.Id.Equals(ancestor.Parent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            parent = ancestor.Parent;
        }
        return false;
    }

    private static IReadOnlyList<ThxDriverApiNode> ReadInventory()
    {
        using SafeDeviceInfoSet devices = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 6);
        if (devices.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var result = new List<ThxDriverApiNode>();
        for (uint index = 0; index < MaximumDevices; index++)
        {
            var data = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            if (!SetupDiEnumDeviceInfo(devices, index, ref data))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 259)
                {
                    return result.AsReadOnly();
                }
                throw new Win32Exception(error);
            }
            string id = ReadInstanceId(devices, ref data);
            string[] hardwareIds = ReadList(data.DevInst, DeviceFormat, 3);
            if (!IsRelevant(id, hardwareIds))
            {
                continue;
            }
            if (CM_Get_DevNode_Status(out uint status, out uint problem, data.DevInst, 0) != 0)
            {
                throw new InvalidDataException("THX device status was not readable.");
            }
            string publishedInf = ReadString(data.DevInst, DriverFormat, 5);
            result.Add(new(id,
                ReadString(data.DevInst, NodeFormat, 8),
                hardwareIds,
                status,
                problem,
                publishedInf.Length == 0 ? string.Empty : WindowsThxBaseAudioAdmission.ResolveStoreInf(publishedInf)));
        }
        throw new InvalidDataException("The THX device inventory exceeded its bound.");
    }

    internal static bool IsRelevant(string instanceId, IReadOnlyList<string> hardwareIds) =>
        instanceId.StartsWith("SWC\\VEN_THX&", StringComparison.OrdinalIgnoreCase)
        || hardwareIds.Any(value => value.StartsWith("SWC\\VEN_THX&", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("INTELAUDIO\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("SOUNDWIRE\\", StringComparison.OrdinalIgnoreCase));

    private static string ReadInstanceId(SafeDeviceInfoSet devices, ref DeviceInfoData data)
    {
        var buffer = new char[200];
        if (!SetupDiGetDeviceInstanceId(devices, ref data, buffer, (uint)buffer.Length, out uint required))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return WindowsThxAdmissionQueries.ReadTerminatedString(buffer, checked((int)required));
    }

    private static byte[] ReadProperty(uint node, Guid format, uint property, out uint type)
    {
        var key = new PropertyKey(format, property);
        byte[] buffer = new byte[65536];
        uint length = (uint)buffer.Length;
        uint status = CM_Get_DevNode_Property(node, ref key, out type, buffer, ref length, 0);
        if (status == 0x25)
        {
            type = 0;
            return [];
        }
        if (status != 0 || length > buffer.Length)
        {
            throw new InvalidDataException($"THX device property {property} was not readable (0x{status:X8}).");
        }
        return buffer[..checked((int)length)];
    }

    private static string ReadString(uint node, Guid format, uint property)
    {
        byte[] value = ReadProperty(node, format, property, out uint type);
        if (type == 0)
        {
            return string.Empty;
        }
        if (type != 0x12 || value.Length < 2 || value.Length % 2 != 0)
        {
            throw new InvalidDataException("Invalid THX device string.");
        }
        char[] text = new UnicodeEncoding(false, false, true).GetChars(value);
        return WindowsThxAdmissionQueries.ReadTerminatedString(text, text.Length);
    }

    private static string[] ReadList(uint node, Guid format, uint property)
    {
        byte[] value = ReadProperty(node, format, property, out uint type);
        return ParseHardwareIds(value, type);
    }

    internal static string[] ParseHardwareIds(byte[] value, uint type) =>
        type == 0 || type == 0x2012 && value is [0, 0]
            ? []
            : WindowsThxAdmissionQueries.ParseHardwareIds(value, (uint)value.Length, type);

    private static void RejectReparseChain(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("THX driver package paths may not contain reparse points.");
            }
        }
    }

    private sealed record RuntimePackageContract(
        string Role,
        string InfName,
        IReadOnlyList<string> HardwareIds,
        bool IsService,
        bool IsApo,
        IReadOnlyList<string>? PackageMembers = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData { internal uint Size; internal Guid ClassGuid; internal uint DevInst; internal UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint property) { internal readonly Guid Format = format; internal readonly uint Property = property; }
    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeDeviceInfoSet SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSet devices, uint index, ref DeviceInfoData device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(SafeDeviceInfoSet devices, ref DeviceInfoData device,
        [Out] char[] id, uint size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint bytes, uint flags);
}

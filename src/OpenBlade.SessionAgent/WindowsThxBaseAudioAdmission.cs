using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record ThxBaseAudioAdmissionSnapshot(bool ExactRoot, bool Ready, bool CanInstall, string Target, string Identity);
internal sealed record ThxBaseAudioNode(string Id, string Parent, IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds, uint Status, uint Problem, string PublishedInf, string StoreInf,
    string Section, string Provider, string Version, string Service, string Class);

// Provisioning-only, observational admission. Runtime admission deliberately still requires a codec.
internal static class WindowsThxBaseAudioAdmission
{
    private const string IntelRoot = @"PCI\VEN_8086&DEV_E328&SUBSYS_30101A58";
    private const string IntelOed = @"INTELAUDIO\DSP_CTLR_DEV_E328&VEN_8086&DEV_0222&SUBSYS_30101A58";
    private const string IntelSdw = @"INTELAUDIO\SDW_CTLR_DEV_E328&VEN_8086&DEV_0333&SUBSYS_30101A58";
    private const int MaximumDevices = 8192;
    private static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DriverFormat = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid NodeFormat = new("4340a6c5-93fa-4706-972c-7b648008a5a7");

    internal static ThxBaseAudioAdmissionSnapshot Capture(ThxAudioCompatibilityProfile profile)
    {
        try
        {
            ThxAudioCompatibilityProfile? host = WindowsThxAudioAdmissionSource.GetExactHostProfile();
            if (host is null || !Eq(host.ModelNumber, profile.ModelNumber))
            {
                return new(false, false, false, string.Empty, "Exact host identity was not confirmed.");
            }

            return Evaluate(profile, ReadInventory(profile), VerifyPinnedPackage, VerifyInbox);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidDataException or UnauthorizedAccessException
            or CryptographicException or InvalidOperationException or ArgumentException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.WindowsThxBaseAudioAdmission",
                "ReadInventory",
                exception);
            return new(false, false, false, string.Empty,
                $"Base-audio inventory was not readable ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
        }
    }

    internal static ThxBaseAudioAdmissionSnapshot Evaluate(ThxAudioCompatibilityProfile profile,
        IReadOnlyList<ThxBaseAudioNode> nodes,
        Func<ThxBaseAudioNode, ThxBaseAudioRecipe, string> verifyPinned,
        Func<string, string, string, string> verifyInbox)
    {
        var evidence = new List<string>();
        string target = string.Empty;
        bool exact = false;
        ThxBaseAudioAdmissionSnapshot Result(bool ready, bool install, string reason) =>
            new(exact, ready, install, target, reason + " | " + string.Join(" | ", evidence));
        try
        {
            if (nodes.Count > MaximumDevices || nodes.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodes.Count)
            {
                return Result(false, false, "The device inventory is oversized or has duplicate identities.");
            }

            if (!nodes.Any(n => ThxAudioCompatibility.MatchesUsb(profile, n.Id)))
            {
                return Result(false, false, "The exact model USB identity is absent.");
            }

            bool intel = profile.ModelNumber == "RZ09-0581";
            if (!intel && profile.ModelNumber is not ("RZ09-0528" or "RZ09-0530"))
            {
                return Result(false, false, "This model has no reviewed base-audio recipe.");
            }

            ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel(profile.ModelNumber);
            string expected = intel ? IntelRoot : profile.CodecHardwareIds.Single();
            // PnPUtil /install considers all matches. A second or foreign matching controller
            // must not become an incidental install target. Revisions are provenance only.
            ThxBaseAudioNode[] roots = nodes.Where(n => MatchesInstallRoot(n.HardwareIds, n.CompatibleIds, recipe.RootHardwareIds)).ToArray();
            if (roots.Length != 1 || !HasHardware(roots[0], expected))
            {
                return Result(false, false, "The install root is missing, duplicated, or belongs to another model.");
            }

            ThxBaseAudioNode root = roots[0];
            if (string.IsNullOrEmpty(root.Parent))
            {
                return Result(false, false, "The root parent identity is unreadable.");
            }

            target = root.Id;
            exact = true;
            var relevant = nodes.Where(n => IsAudio(n) || Eq(n.Id, root.Id)).OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            // A hash records the entire private topology/binding observation, including status,
            // without putting device instance identifiers into human-facing diagnostics.
            evidence.Add("inventory=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(relevant)))));
            void Observe(ThxBaseAudioNode n) => evidence.Add($"{Path.GetFileName(n.StoreInf)}:{n.Version}:{n.Section}:{n.Service}:problem={n.Problem}");
            bool Pinned(ThxBaseAudioNode n, string inf, string section, string service, string provider, string version, string deviceClass)
            {
                Observe(n);
                if (!Healthy(n) || !Eq(Path.GetFileName(n.StoreInf), inf) || !Eq(n.Section, section)
                    || !Eq(n.Service, service) || !Eq(n.Provider, provider) || !Eq(n.Version, version) || !Eq(n.Class, deviceClass))
                {
                    return false;
                }

                evidence.Add("verifying=" + Path.GetFileName(n.StoreInf));
                evidence.Add(verifyPinned(n, recipe));
                return true;
            }
            bool Inbox(ThxBaseAudioNode n, string inf, string section, string service, string deviceClass, string matchingId)
            {
                Observe(n);
                if (!Healthy(n) || !Eq(Path.GetFileName(n.StoreInf), inf) || !Eq(n.Section, section)
                    || !Eq(n.Service, service) || !Eq(n.Provider, "Microsoft") || !Eq(n.Class, deviceClass)
                    || !n.HardwareIds.Concat(n.CompatibleIds).Any(id => Eq(id, matchingId)))
                {
                    return false;
                }

                evidence.Add("verifying=" + inf);
                evidence.Add(verifyInbox(inf, Path.ChangeExtension(inf, ".sys"), n.StoreInf));
                return true;
            }
            if (!intel)
            {
                Observe(root);
                if (Unbound(root))
                {
                    return Result(false, true, "The exact Realtek codec is present without a driver (problem 28).");
                }

                if (Eq(Path.GetFileName(root.StoreInf), "hdaudio.inf"))
                {
                    bool basic = Inbox(root, "hdaudio.inf", "HdAudModel", "HdAudAddService", "MEDIA", @"HDAUDIO\FUNC_01");
                    return Result(false, basic, basic ? "The exact codec uses the trusted Microsoft basic audio driver." : "The basic audio binding is unrecognized or unhealthy.");
                }
                // Both reviewed Realtek model sections use the same service, provider and build.
                const string section = "IntcAzAudModel.NTamd64";
                bool ready = Pinned(root, "HDXACPRazer.inf", section, "IntcAzAudAddService", "Realtek Semiconductor Corp.", "6.0.9906.1", "MEDIA");
                return Result(ready, false, ready ? "The bound Realtek base package is complete and trusted." : "The existing Realtek binding is not the reviewed package; preserve it.");
            }
            // Windows supplies these files independently of the vendor CAB. Check current
            // servicing-catalog trust, without pinning a Windows build or redistributing them.
            foreach (string inf in new[] { "sdcaclass.inf", "sdcamfd.inf", "sdcaaggregator.inf" })
            {
                evidence.Add("verifying=" + inf);
                evidence.Add(verifyInbox(inf, Path.ChangeExtension(inf, ".sys"), string.Empty));
            }

            var graph = relevant.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            bool Descendant(ThxBaseAudioNode n, ThxBaseAudioNode ancestor)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int depth = 0; depth < 16 && seen.Add(n.Id); depth++)
                {
                    if (Eq(n.Parent, ancestor.Id))
                    {
                        return true;
                    }

                    if (!graph.TryGetValue(n.Parent, out ThxBaseAudioNode? parent))
                    {
                        return false;
                    }

                    n = parent;
                }
                return false;
            }
            ThxBaseAudioNode[] oedNodes = relevant.Where(n => HasHardware(n, IntelOed)).ToArray();
            ThxBaseAudioNode[] sdwNodes = relevant.Where(n => HasHardware(n, IntelSdw)).ToArray();
            if (oedNodes.Length > 1 || sdwNodes.Length > 1
                || oedNodes.Any(n => !Eq(n.Parent, root.Id))
                || sdwNodes.Any(n => oedNodes.Length != 1 || !Eq(n.Parent, oedNodes[0].Id)))
            {
                return Result(false, false, "The Intel controller ancestry is duplicated or inconsistent.");
            }

            if (Unbound(root))
            {
                Observe(root);
                bool childrenAbsent = !relevant.Any(n => !Eq(n.Id, root.Id) && (n.Id.StartsWith("INTELAUDIO\\", StringComparison.OrdinalIgnoreCase)
                    || n.Id.StartsWith("SOUNDWIRE\\", StringComparison.OrdinalIgnoreCase)));
                return Result(false, childrenAbsent, childrenAbsent ? "The exact PCI audio root is unbound (problem 28); its children are not yet enumerated." : "An unbound PCI root has inconsistent audio children.");
            }
            if (!Pinned(root, "IntcAudioBus.inf", "IntcAudioBusPTLplus.NT", "IntcAudioBus", "Intel(R) Corporation", "20.43.12634.0", "System"))
            {
                return Result(false, false, "The existing Intel root binding is unknown, unhealthy, or changed; preserve it.");
            }

            bool incomplete = oedNodes.Length == 0 || sdwNodes.Length == 0;
            foreach (ThxBaseAudioNode child in relevant.Where(n => n.Id.StartsWith("INTELAUDIO\\", StringComparison.OrdinalIgnoreCase)))
            {
                if (!Descendant(child, root))
                {
                    return Result(false, false, "An Intel audio child has unconfirmed root ancestry.");
                }

                if (Unbound(child)) { Observe(child); incomplete = true; continue; }
                string original = Path.GetFileName(child.StoreInf);
                (string section, string service, string deviceClass) = original.ToLowerInvariant() switch
                {
                    "intcoed.inf" => ("IntcOED_PTL_11.NT", "IntcOED", "System"),
                    "intcsdwbus.inf" => ("IntcSdwBus.NT", "IntcSdwBus", "System"),
                    "intcdmic.inf" => ("IntcAudModel.NT", "IntcDMic", "MEDIA"),
                    "intcbtau.inf" => ("IntcAudModel.NT", "IntcBTAu", "MEDIA"),
                    "intcusb.inf" => ("IntcAudModel.NT", "IntcUSB", "MEDIA"),
                    "intcsst.inf" => ("IntcAudModel.NT", "IntcSST", "MEDIA"),
                    "intcsbtle.inf" => ("Audio_Device.NT", "IntcSBtLe", "MEDIA"),
                    "intcssdw.inf" => ("Audio_Device.NT", "IntcSSdw", "MEDIA"),
                    // Pinned DetectionVerificationDrv.inf E328 Win11 model/section; its version is independent of SST.
                    "detectionverificationdrv.inf" => ("IntcDVWin11.NT", "WUDFRd", "System"),
                    _ => (string.Empty, string.Empty, string.Empty),
                };
                string version = Eq(original, "DetectionVerificationDrv.inf") ? "1.0.4139.0" : "20.43.12634.0";
                if (section.Length == 0 || !Pinned(child, original, section, service, "Intel(R) Corporation", version, deviceClass))
                {
                    return Result(false, false, $"The existing Intel child {original}:{child.Version}:{child.Section}:{child.Service} is not reviewed; preserve it.");
                }
            }
            // A partially recognized Intel graph can be completed once. Any unknown bound
            // child above blocks repair. The caller must independently read back after install.
            if (incomplete)
            {
                return Result(false, !relevant.Any(n => n.Id.StartsWith("SOUNDWIRE\\", StringComparison.OrdinalIgnoreCase)), "The recognized Intel graph is incomplete; existing SoundWire nodes require complete controller ancestry.");
            }

            ThxBaseAudioNode sdw = sdwNodes.Single();
            ThxBaseAudioNode[] codecs = relevant.Where(n => ThxAudioCompatibility.MatchesCodec(profile, n.HardwareIds)).ToArray();
            if (codecs.Length == 0)
            {
                return Result(false, false, "The Intel drivers are present but the exact codec graph is missing.");
            }

            foreach (ThxBaseAudioNode codec in codecs)
            {
                if (!graph.TryGetValue(codec.Parent, out ThxBaseAudioNode? parent) || !Descendant(codec, sdw))
                {
                    return Result(false, false, "The exact codec has unconfirmed SoundWire ancestry.");
                }

                if (codec.HardwareIds.Any(id => Eq(id, @"SDCA\AGGREGATEDSPEAKER")))
                {
                    if (!Inbox(codec, "sdcaaggregator.inf", "SdcaAggregator_ChildDevice.NT", "", "MEDIA", @"SDCA\AGGREGATEDSPEAKER")
                        || !Eq(parent.Parent, sdw.Id)
                        || !Inbox(parent, "sdcaaggregator.inf", "SdcaAggregator_Device.NT", "SdcaAggregatorInbox", "MEDIA", @"SOUNDWIRE\SDCA_AGGREGATOR"))
                    {
                        return Result(false, false, "The exact aggregated codec binding is unrecognized.");
                    }
                }
                else
                {
                    string codecId = codec.HardwareIds.First(id => profile.CodecHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase));
                    string functionId = codecId[..codecId.LastIndexOf("&DynamicEnumSpeaker0", StringComparison.OrdinalIgnoreCase)];
                    string? peripheralPart = ObservedPeripheralPart(functionId);
                    if (peripheralPart is null || !parent.HardwareIds.Any(id => Eq(id, functionId)))
                    {
                        return Result(false, false, "The dynamic codec parent is a different audio function.");
                    }

                    string function = parent.CompatibleIds.FirstOrDefault(id => Eq(id, @"SDCA_10\AUDIOFUNCTION") || Eq(id, @"SDCA_11\AUDIOFUNCTION")) ?? string.Empty;
                    if (!Inbox(codec, "sdcaclass.inf", "SdcaClass_Device_Child.NT", "", "MEDIA", @"SDCA\DynamicEnumChild")
                        || function.Length == 0 || !Inbox(parent, "sdcaclass.inf", "SdcaClass_Device.NT", "SdcaClassInbox", "MEDIA", function)
                        || !graph.TryGetValue(parent.Parent, out ThxBaseAudioNode? peripheral) || !Eq(peripheral.Parent, sdw.Id)
                        || !peripheral.HardwareIds.Any(id => id.StartsWith(@"SOUNDWIRE\SDCA_PERIPHERAL&MAN_025D&PART_" + peripheralPart + "&", StringComparison.OrdinalIgnoreCase)
                            && id.Split('&').Contains("SUBSYS_30101A58", StringComparer.OrdinalIgnoreCase))
                        || !Inbox(peripheral, "sdcamfd.inf", "SdcaMfd_Device.NT", "SdcaMfdInbox", "System", @"SOUNDWIRE\SDCA_PERIPHERAL"))
                    {
                        return Result(false, false, "The exact dynamic codec binding or peripheral ancestry is unrecognized.");
                    }
                }
            }
            if (!codecs.Any(n => n.HardwareIds.Any(id => Eq(id, @"SDCA\DynamicEnumChild")))
                || !codecs.Any(n => n.HardwareIds.Any(id => Eq(id, @"SDCA\AGGREGATEDSPEAKER"))))
            {
                return Result(false, false, "The exact dynamic and aggregated codec roles are not both present.");
            }

            return Result(true, false, "The bound Intel and Windows SDCA audio graph is complete and trusted.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException
            or Win32Exception or InvalidOperationException or ArgumentException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.WindowsThxBaseAudioAdmission",
                "VerifyPackages",
                exception);
            return Result(false, false, $"Base-audio package or inbox trust was not confirmed ({exception.GetType().Name}, 0x{exception.HResult:X8})." + (exception is InvalidDataException ? " " + exception.Message : ""));
        }
    }

    // Exact read-only RZ09-0581 topology, independently observed on 2026-10-20:
    // these three function devnodes are children of the listed Realtek peripheral.
    // FUNC and PART equality is not assumed for other SDCA functions or aliases.
    // Peripheral revision/version remains provenance, not a driver prerequisite.
    private static string? ObservedPeripheralPart(string functionId) => functionId.ToUpperInvariant() switch
    {
        @"SOUNDWIRE\SDCA_FUNCTION_10&MAN_025D&FUNC_0721&TYPE_06&VER_01&ADR_01&LID_03&UID_00&SUBSYS_30101A58" => "0721",
        @"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58" => "0721",
        @"SOUNDWIRE\SDCA_FUNCTION_10&MAN_025D&FUNC_1320&TYPE_01&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58" => "1320",
        _ => null,
    };
    internal static bool MatchesInstallRoot(IReadOnlyList<string> hardwareIds, IReadOnlyList<string> compatibleIds, IReadOnlyList<string> matches) =>
        hardwareIds.Concat(compatibleIds).Any(id => matches.Contains(id, StringComparer.OrdinalIgnoreCase));
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool HasHardware(ThxBaseAudioNode n, string id) => n.HardwareIds.Any(value => Eq(value, id)
        || value.StartsWith(id + "&REV_", StringComparison.OrdinalIgnoreCase));
    private static bool Healthy(ThxBaseAudioNode n) => n.Problem == 0 && (n.Status & 0x400) == 0
        && ((n.Status & 8) != 0 || n.Service.Length == 0) && n.PublishedInf.Length > 0 && n.StoreInf.Length > 0;
    private static bool Unbound(ThxBaseAudioNode n) => n.Problem == 28 && n.PublishedInf.Length == 0
        && n.StoreInf.Length == 0 && n.Service.Length == 0 && n.Section.Length == 0 && n.Provider.Length == 0 && n.Version.Length == 0;
    private static bool IsAudio(ThxBaseAudioNode n) => new[] { "PCI\\", "HDAUDIO\\", "INTELAUDIO\\", "SOUNDWIRE\\" }
        .Any(prefix => n.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string VerifyPinnedPackage(ThxBaseAudioNode node, ThxBaseAudioRecipe recipe)
    {
        string original = Path.GetFileName(node.StoreInf);
        ThxBaseAudioDriverPackage package = recipe.Packages.Single(p => Eq(p.InfName, original));
        string root = Path.GetDirectoryName(node.StoreInf)!;
        var files = package.Files.Concat(recipe.Files.Where(f => Eq(f.Name, package.CatalogFile)))
            .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var locks = new List<FileStream>();
        try
        {
            foreach (ThxBaseAudioFile file in files)
            {
                string path = Path.Combine(root, file.Name);
                RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(stream);
                if (stream.Length != file.Length || !Eq(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256))
                {
                    throw new InvalidDataException($"Bound member {file.Name} differs from its reviewed hash.");
                }
            }
            RazerDriverPackageTrust.VerifyCatalogMembers(root, package.CatalogFile,
                package.Files.Where(f => !Eq(f.Name, package.CatalogFile)).Select(f => f.Name), CancellationToken.None, cacheOnly: true);
            return original + "=" + string.Join(",", files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.Name + ":" + f.Sha256));
        }
        finally
        {
            foreach (FileStream stream in locks)
            {
                stream.Dispose();
            }
        }
    }

    private static string VerifyInbox(string inf, string sys, string boundStoreInf)
    {
        string storeInf = boundStoreInf.Length == 0 ? ResolveStoreInf(inf) : boundStoreInf;
        if (!Eq(Path.GetFileName(storeInf), inf))
        {
            throw new InvalidDataException("Unexpected inbox original INF.");
        }

        string root = Path.GetDirectoryName(storeInf)!;
        return WindowsInboxAudioTrust.VerifyFile(storeInf) + "," + WindowsInboxAudioTrust.VerifyFile(Path.Combine(root, sys));
    }

    internal static string ResolveStoreInf(string publishedInf)
    {
        if (Path.GetFileName(publishedInf) != publishedInf || !publishedInf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid published INF name.");
        }

        var buffer = new char[260];
        if (!SetupGetInfDriverStoreLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", publishedInf),
            IntPtr.Zero, null, buffer, (uint)buffer.Length, out uint required))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        string path = WindowsThxAdmissionQueries.ReadTerminatedString(buffer, checked((int)required));
        string store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(store, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The bound INF is outside the Windows driver store.");
        }

        RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
        return path;
    }

    private static IReadOnlyList<ThxBaseAudioNode> ReadInventory(ThxAudioCompatibilityProfile profile)
    {
        using SafeDeviceInfoSet devices = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 6);
        if (devices.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = new List<ThxBaseAudioNode>();
        IReadOnlyList<string> rootMatches = ThxBaseAudioPackage.ForModel(profile.ModelNumber).RootHardwareIds;
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
            var idBuffer = new char[200];
            if (!SetupDiGetDeviceInstanceId(devices, ref data, idBuffer, (uint)idBuffer.Length, out uint chars))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string id = WindowsThxAdmissionQueries.ReadTerminatedString(idBuffer, checked((int)chars));
            bool audio = new[] { "PCI\\", "HDAUDIO\\", "INTELAUDIO\\", "SOUNDWIRE\\" }.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!audio)
            {
                if (id.StartsWith("USB\\VID_1532&", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new(id, "", [], [], 0, 0, "", "", "", "", "", "", ""));
                }

                continue;
            }
            string[] hardware = ReadList(data.DevInst, DeviceFormat, 3);
            string[] compatible = ReadList(data.DevInst, DeviceFormat, 4);
            // Only root matches and the Intel audio descendants can affect this query.
            if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                && !MatchesInstallRoot(hardware, compatible, rootMatches))
            {
                continue;
            }

            if (id.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase)
                && !MatchesInstallRoot(hardware, compatible, rootMatches))
            {
                continue;
            }

            if (CM_Get_DevNode_Status(out uint status, out uint problem, data.DevInst, 0) != 0)
            {
                throw new InvalidDataException("Device status was not readable.");
            }

            string published = ReadString(data.DevInst, DriverFormat, 5);
            string parent = ReadString(data.DevInst, NodeFormat, 8);
            if (parent.Length == 0)
            {
                throw new InvalidDataException("Audio node parent was not readable.");
            }

            result.Add(new(id, parent, hardware, compatible, status, problem,
                published, published.Length == 0 ? "" : ResolveStoreInf(published), ReadString(data.DevInst, DriverFormat, 6),
                ReadString(data.DevInst, DriverFormat, 9), ReadString(data.DevInst, DriverFormat, 3),
                ReadString(data.DevInst, DeviceFormat, 6), ReadString(data.DevInst, DeviceFormat, 9)));
        }
        throw new InvalidDataException("The device inventory exceeded its bound.");
    }

    private static byte[] ReadProperty(uint node, Guid format, uint property, out uint type)
    {
        var key = new PropertyKey(format, property);
        byte[] buffer = new byte[65536];
        uint length = (uint)buffer.Length;
        uint status = CM_Get_DevNode_Property(node, ref key, out type, buffer, ref length, 0);
        if (status == 0x25) { type = 0; return []; }
        if (status != 0 || length > buffer.Length)
        {
            throw new InvalidDataException($"Device property {property} was not readable (0x{status:X8}).");
        }

        return buffer[..checked((int)length)];
    }
    private static string ReadString(uint node, Guid format, uint property)
    {
        byte[] value = ReadProperty(node, format, property, out uint type);
        if (type == 0)
        {
            return "";
        }

        if (type != 0x12 || value.Length < 2 || value.Length % 2 != 0)
        {
            throw new InvalidDataException("Invalid device string.");
        }

        char[] text = new UnicodeEncoding(false, false, true).GetChars(value);
        return WindowsThxAdmissionQueries.ReadTerminatedString(text, text.Length);
    }
    private static string[] ReadList(uint node, Guid format, uint property)
    {
        byte[] value = ReadProperty(node, format, property, out uint type);
        return type == 0 ? [] : property == 4 ? ParseCompatibleIds(value, type)
            : WindowsThxAdmissionQueries.ParseHardwareIds(value, (uint)value.Length, type);
    }
    internal static string[] ParseCompatibleIds(byte[] value, uint type)
    {
        // CM_Get_DevNode_Property returns an empty CompatibleIds MULTI_SZ as
        // one UTF-16 NUL (type 0x2012, length 2) on the observed SDCA leaves.
        // This exception belongs only to the optional compatible-ID property.
        if (type == 0x2012 && value.Length == 2 && value[0] == 0 && value[1] == 0)
        {
            return [];
        }
        return WindowsThxAdmissionQueries.ParseHardwareIds(value, (uint)value.Length, type);
    }
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
    private static extern bool SetupDiGetDeviceInstanceId(SafeDeviceInfoSet devices, ref DeviceInfoData device, [Out] char[] id, uint size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocation(string file, IntPtr platform, string? locale, [Out] char[] buffer, uint size, out uint required);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type, [Out] byte[] buffer, ref uint bytes, uint flags);
}

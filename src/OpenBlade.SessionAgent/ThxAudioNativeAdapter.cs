using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal interface IThxAudioNativeAdapter : IDisposable
{
    bool AddDevice(string deviceJson);

    bool CheckStatus(string deviceJson);

    bool RemoveDevice(string deviceJson);

    string GetProperty(string deviceJson, string propertyId);

    bool SetProperty(
        string deviceJson,
        string propertyId,
        string jsonValue,
        string? propertyType);

    string GetPresetList(string deviceJson, string presetGroupId);

    string GetSelectedPreset(string deviceJson, string presetGroupId);

    bool SelectPreset(string deviceJson, string presetGroupId, string presetId);
}

internal sealed class ThxAudioNativeAdapter : IThxAudioNativeAdapter
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint GetModuleHandleExFlagPin = 0x00000001;
    private const uint GetModuleHandleExFlagFromAddress = 0x00000004;
    private const int MaximumNativeJsonBytes = 64 * 1024;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool DeviceCall(nint deviceJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetValueCall(nint deviceJson, nint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SetValueCall(
        nint deviceJson,
        nint id,
        nint value,
        nint propertyType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SelectPresetCall(nint deviceJson, nint groupId, nint presetId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeMallocCall(nint value);

    private readonly SafeLibraryHandle library;
    private readonly DeviceCall addDevice;
    private readonly DeviceCall checkStatus;
    private readonly DeviceCall removeDevice;
    private readonly GetValueCall getProperty;
    private readonly SetValueCall setProperty;
    private readonly GetValueCall getPresetList;
    private readonly GetValueCall getSelectedPreset;
    private readonly SelectPresetCall selectPreset;
    private readonly FreeMallocCall freeMalloc;

    internal ThxAudioNativeAdapter()
    {
        string path = WindowsThxAudioAdmissionSource.GetNativeDllPath();
        using var pinnedFile = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(WindowsThxAudioAdmissionSource.NativeDllSha256),
                SHA256.HashData(pinnedFile)))
        {
            throw new InvalidOperationException(
                "The pinned THX native component is unavailable or has changed.");
        }
        // Keep the verified file open without write/delete sharing until the
        // loader maps that same path, closing the hash/load replacement race.
        library = LoadLibraryExW(path, nint.Zero, LoadLibrarySearchSystem32);
        if (library.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            library.Dispose();
            throw new InvalidOperationException(
                $"The pinned THX native component could not be loaded (0x{error:X8}).");
        }

        try
        {
            // The admitted native client can leave worker threads running after
            // RemoveDevice. Runtime validation observed an execute fault in its
            // unloaded image during FreeLibrary. This adapter runs only in the
            // bounded one-shot helper: pin the image until process exit so those
            // threads cannot execute unmapped code. The OS reclaims the image
            // with the helper; Dispose still releases our owned loader handle.
            if (!GetModuleHandleExW(
                    GetModuleHandleExFlagPin | GetModuleHandleExFlagFromAddress,
                    library.DangerousGetHandle(),
                    out _))
            {
                throw new InvalidOperationException(
                    "The THX native component could not be pinned to its helper lifetime.");
            }
            addDevice = GetExport<DeviceCall>("AddDevice");
            checkStatus = GetExport<DeviceCall>("CheckStatus");
            removeDevice = GetExport<DeviceCall>("RemoveDevice");
            getProperty = GetExport<GetValueCall>("GetProperty");
            setProperty = GetExport<SetValueCall>("SetProperty");
            getPresetList = GetExport<GetValueCall>("GetPresetList");
            getSelectedPreset = GetExport<GetValueCall>("GetSelectedPreset");
            selectPreset = GetExport<SelectPresetCall>("SelectPreset");
            freeMalloc = GetExport<FreeMallocCall>("FreeMalloc");
        }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    public bool AddDevice(string deviceJson) => Invoke(deviceJson, addDevice);

    public bool CheckStatus(string deviceJson) => Invoke(deviceJson, checkStatus);

    public bool RemoveDevice(string deviceJson) => Invoke(deviceJson, removeDevice);

    public string GetProperty(string deviceJson, string propertyId) =>
        GetJson(deviceJson, propertyId, getProperty);

    public bool SetProperty(
        string deviceJson,
        string propertyId,
        string jsonValue,
        string? propertyType)
    {
        using Utf8Argument device = new(deviceJson);
        using Utf8Argument property = new(propertyId);
        using Utf8Argument value = new(jsonValue);
        using Utf8Argument? type = propertyType is null
            ? null
            : new Utf8Argument(propertyType);
        return setProperty(
            device.Pointer,
            property.Pointer,
            value.Pointer,
            type?.Pointer ?? nint.Zero);
    }

    public string GetPresetList(string deviceJson, string presetGroupId) =>
        GetJson(deviceJson, presetGroupId, getPresetList);

    public string GetSelectedPreset(string deviceJson, string presetGroupId) =>
        GetJson(deviceJson, presetGroupId, getSelectedPreset);

    public bool SelectPreset(string deviceJson, string presetGroupId, string presetId)
    {
        using Utf8Argument device = new(deviceJson);
        using Utf8Argument group = new(presetGroupId);
        using Utf8Argument preset = new(presetId);
        return selectPreset(device.Pointer, group.Pointer, preset.Pointer);
    }

    public void Dispose() => library.Dispose();

    private bool Invoke(string value, DeviceCall call)
    {
        using Utf8Argument argument = new(value);
        return call(argument.Pointer);
    }

    private string GetJson(string deviceJson, string id, GetValueCall call)
    {
        using Utf8Argument device = new(deviceJson);
        using Utf8Argument identifier = new(id);
        nint result = call(device.Pointer, identifier.Pointer);
        if (result == nint.Zero)
        {
            throw new InvalidDataException("The THX native component returned no JSON value.");
        }

        try
        {
            int length = 0;
            while (length < MaximumNativeJsonBytes && Marshal.ReadByte(result, length) != 0)
            {
                length++;
            }
            if (length == MaximumNativeJsonBytes)
            {
                throw new InvalidDataException("The THX native JSON value exceeds 64 KiB.");
            }

            byte[] bytes = new byte[length];
            Marshal.Copy(result, bytes, 0, length);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        finally
        {
            freeMalloc(result);
        }
    }

    private T GetExport<T>(string name)
        where T : Delegate
    {
        nint address = GetProcAddress(library, name);
        if (address == nint.Zero)
        {
            throw new EntryPointNotFoundException(
                $"The pinned THX native component does not export {name}.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeLibraryHandle LoadLibraryExW(
        string fileName,
        nint file,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(SafeLibraryHandle module, string procedureName);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, nint moduleAddress, out nint module);

    private sealed class SafeLibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeLibraryHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FreeLibrary(handle);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

    private sealed class Utf8Argument : IDisposable
    {
        private nint pointer;

        internal Utf8Argument(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            pointer = Marshal.StringToCoTaskMemUTF8(value);
        }

        internal nint Pointer => pointer;

        public void Dispose()
        {
            nint released = Interlocked.Exchange(ref pointer, nint.Zero);
            if (released != nint.Zero)
            {
                Marshal.FreeCoTaskMem(released);
            }
        }
    }
}

internal static class ThxVendorJson
{
    internal static bool ParseBoolean(string json)
    {
        using JsonDocument document = Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.True
            ? true
            : document.RootElement.ValueKind == JsonValueKind.False
                ? false
                : throw new InvalidDataException("The THX property is not a Boolean value.");
    }

    internal static IReadOnlyList<string> ParsePresetIds(string json)
    {
        using JsonDocument document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The THX preset list is not an array.");
        }

        var presets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String
                || element.GetString() is not { } id
                || !ValidId(id)
                || !seen.Add(id))
            {
                throw new InvalidDataException("The THX preset list contains an invalid ID.");
            }
            presets.Add(id);
            if (presets.Count > SessionAudioIpc.MaximumPresets)
            {
                throw new InvalidDataException("The THX preset list exceeds the supported limit.");
            }
        }
        return presets;
    }

    internal static double ParseFloat(string json, double minimum, double maximum)
    {
        using JsonDocument document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Number
            || !document.RootElement.TryGetDouble(out double value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException("The THX property is not a valid Float value.");
        }
        return value;
    }

    internal static string? ParseSelectedPreset(string json)
    {
        // The pinned DLL returned the bare preset ID "music" at runtime,
        // while cached middleware also describes JSON string/null responses.
        // Accept bounded plain IDs as well; ReadState still requires membership
        // in the catalog returned by this same endpoint.
        if (json != "null" && ValidId(json)
            && json.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-' or '.'))
        {
            return json;
        }
        using JsonDocument document = Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (document.RootElement.ValueKind != JsonValueKind.String
            || document.RootElement.GetString() is not { } id
            || !ValidId(id))
        {
            throw new InvalidDataException("The selected THX preset ID is invalid.");
        }
        return id;
    }

    private static JsonDocument Parse(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > SessionAudioIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("The THX native JSON value is invalid.");
        }
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The THX native JSON value is malformed.", exception);
        }
    }

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);
}

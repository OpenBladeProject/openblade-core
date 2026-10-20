using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

/// <summary>Read-only product736 output selection; never activates an audio control interface.</summary>
internal static partial class WindowsThxActiveOutput
{
    // Product736 middleware wB, SHA256 26022B8618DDE19E54A3B2C386E5CD16E1D62F036C05B01A1E53E8B6540F9888,
    // uses this endpoint friendly-name pattern and container sentinel. The default
    // endpoint must actually match; the vendor's implicit built-in fallback is not used.
    private static readonly Guid BuiltInContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    // Windows SDK 10.0.26100.0 functiondiscoverykeys_devpkey.h, PKEY_Device_*.
    private static readonly PropertyKey FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    [GeneratedRegex(@"\((\d+- )?Realtek Audio Jack.*\)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ProductEndpointName();

    internal static ThxAudioEndpoint? Classify(string? name, Guid? container)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024
            || container is null || container == Guid.Empty || !ProductEndpointName().IsMatch(name))
        {
            return null;
        }
        return container == BuiltInContainer ? ThxAudioEndpoint.Speakers : ThxAudioEndpoint.Headphones;
    }

    private static readonly PropertyKey FormFactor = new(new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private static readonly PropertyKey ThxProduct = new(new("4a82dc8f-2acc-466f-b11a-3fbf40a339a0"), 0);
    private static readonly PropertyKey ThxEndpoint = new(new("4a82dc8f-2acc-466f-b11a-3fbf40a339a0"), 2);

    private sealed record DefaultOutput(string Id, string? Name, Guid? Container, string? Product, string? Endpoint, uint? FormFactor);

    internal static ThxAudioEndpoint? Read()
    {
        if (WindowsThxAudioAdmissionSource.GetExactHostProfile()?.Backend != ThxAudioNativeBackend.V4)
        {
            // Legacy manual control uses explicit endpoint/codec ancestry below.
            // Its automatic application-routing contract is not yet admitted.
            return null;
        }
        DefaultOutput? output = ReadDefaultOutput();
        return output is null ? null : Classify(output.Name, output.Container);
    }

    internal static string? ReadExactDefaultOutputId(ThxAudioCompatibilityProfile profile, ThxAudioEndpoint endpoint)
    {
        DefaultOutput? output = ReadDefaultOutput();
        return output is not null && ClassifyLegacyFormFactor(output.FormFactor) == endpoint
            && WindowsThxAdmissionQueries.HasExactEndpointCodec(output.Id, profile) ? output.Id : null;
    }

    // Windows SDK EndpointFormFactor. Line-level, HDMI, SPDIF and unknown
    // renderers cannot stand in for the selected speakers/headphones output.
    internal static ThxAudioEndpoint? ClassifyLegacyFormFactor(uint? value) => value switch
    {
        1 => ThxAudioEndpoint.Speakers,
        3 or 5 => ThxAudioEndpoint.Headphones,
        _ => null,
    };

    // Advisory only: a stored THX switch does not override Windows bypassing its APO.
    internal static bool? ReadSystemEffectsDisabled(string productId, ThxAudioEndpoint endpoint)
    {
        DefaultOutput? output = ReadDefaultOutput();
        if (output is null || output.Product != productId
            || output.Endpoint != (endpoint == ThxAudioEndpoint.Speakers ? "speakers" : "headphones")
            || !TryGetRenderEndpointId(output.Id, out Guid id))
        {
            return null;
        }
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? effects = machine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\"
                + id.ToString("B") + @"\FxProperties", writable: false);
            return ParseSystemEffectsDisabled(effects?.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5"));
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryGetRenderEndpointId(string value, out Guid id)
    {
        const string prefix = "{0.0.0.00000000}.";
        id = default;
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && value.Length == prefix.Length + 38
            && Guid.TryParseExact(value[prefix.Length..], "B", out id);
    }

    internal static bool? ParseSystemEffectsDisabled(object? value) => value switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    private static DefaultOutput? ReadDefaultOutput()
    {
        object? native = null;
        IMMDevice? endpoint = null;
        IMMDevice? console = null;
        IPropertyStore? properties = null;
        try
        {
            native = CoreAudioDeviceEnumerator.Create();
            var enumerator = (IMMDeviceEnumerator)native;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(CoreAudioDataFlow.Render, CoreAudioRole.Multimedia, out endpoint));
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(CoreAudioDataFlow.Render, CoreAudioRole.Console, out console));
            Marshal.ThrowExceptionForHR(endpoint.GetId(out string endpointId));
            Marshal.ThrowExceptionForHR(console.GetId(out string consoleId));
            // Divergent default roles are ambiguous for the vendor's default-speaker contract.
            if (!string.Equals(endpointId, consoleId, StringComparison.Ordinal) || string.IsNullOrEmpty(endpointId))
            {
                return null;
            }
            Marshal.ThrowExceptionForHR(endpoint.GetState(out uint state));
            if (state != 1)
            {
                return null;
            }
            Marshal.ThrowExceptionForHR(endpoint.OpenPropertyStore(0, out properties)); // STGM_READ
            string? name = ReadString(properties, FriendlyName);
            Guid? container = ReadGuid(properties, ContainerId);
            return new DefaultOutput(endpointId, name, container,
                ReadOptionalString(properties, ThxProduct), ReadOptionalString(properties, ThxEndpoint), ReadOptionalUInt(properties, FormFactor));
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(properties);
            Release(console);
            Release(endpoint);
            Release(native);
        }
    }

    internal static IDisposable Subscribe(Action changed)
    {
        var callback = new Notification(changed);
        return CoreAudioNotificationRegistration.Acquire(
            CoreAudioDeviceEnumerator.Create,
            instance => (IMMDeviceEnumerator)instance,
            instance => Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)instance).RegisterEndpointNotificationCallback(callback)),
            instance => _ = ((IMMDeviceEnumerator)instance).UnregisterEndpointNotificationCallback(callback),
            _ => changed(), () => { }, () => { }, Release);
    }

    private static string? ReadOptionalString(IPropertyStore properties, PropertyKey key)
    {
        try
        {
            return ReadString(properties, key);
        }
        catch
        {
            // Missing advisory metadata must not affect ordinary output selection.
            return null;
        }
    }

    private static string? ReadString(IPropertyStore properties, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
            if (value.Type != 31 || value.Pointer == 0)
            {
                return null;
            }
            for (int length = 0; length <= 1024; length++)
            {
                if (Marshal.ReadInt16(value.Pointer, length * sizeof(char)) == 0)
                {
                    return Marshal.PtrToStringUni(value.Pointer, length);
                }
            }
            return null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private static uint? ReadOptionalUInt(IPropertyStore properties, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
            return value.Type == 19 ? value.UInt32 : null; // VT_UI4
        }
        catch
        {
            // Legacy classification requires this value, while V4 selection
            // retains its own unchanged name/container contract.
            return null;
        }
        finally { _ = PropVariantClear(ref value); }
    }

    private static Guid? ReadGuid(IPropertyStore properties, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
            return value.Type == 72 && value.Pointer != 0 ? Marshal.PtrToStructure<Guid>(value.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.ReleaseComObject(instance);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
        [FieldOffset(8)] internal uint UInt32;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(CoreAudioDataFlow flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(CoreAudioDataFlow flow, CoreAudioRole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, nint parameters, out nint instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(CoreAudioDataFlow flow, CoreAudioRole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Notification(Action changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint state) { changed(); return 0; }
        public int OnDeviceAdded(string id) { changed(); return 0; }
        public int OnDeviceRemoved(string id) { changed(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { changed(); return 0; }
        public int OnDefaultDeviceChanged(CoreAudioDataFlow flow, CoreAudioRole role, string? id)
        {
            if (flow == CoreAudioDataFlow.Render && role is CoreAudioRole.Console or CoreAudioRole.Multimedia)
            {
                changed();
            }
            return 0;
        }
    }
}

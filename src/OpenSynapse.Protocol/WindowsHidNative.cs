using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenSynapse.Protocol;

internal sealed partial class WindowsHidNative : IHidCandidateNative
{
    private const uint Present = 0x00000002;
    private const uint DeviceInterface = 0x00000010;

    public IReadOnlyList<string> EnumerateDevicePaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            Present | DeviceInterface);
        if (deviceSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            List<string> paths = [];
            uint index = 0;
            while (TryGetDevicePath(deviceSet, ref hidGuid, index++, out string? path))
            {
                paths.Add(path);
            }

            return paths;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public unsafe OpenedCandidateHandle? TryOpen(
        string path,
        HidOpenParameters parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SecurityAttributes securityAttributes = new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = parameters.Inheritable,
        };
        IntPtr security = parameters.Inheritable
            ? (IntPtr)(&securityAttributes)
            : IntPtr.Zero;
        SafeFileHandle fileHandle = CreateFile(
            path,
            parameters.DesiredAccess,
            parameters.ShareMode,
            security,
            parameters.CreationDisposition,
            parameters.FlagsAndAttributes,
            IntPtr.Zero);
        if (fileHandle.IsInvalid)
        {
            fileHandle.Dispose();
            return null;
        }

        return new OpenedCandidateHandle(fileHandle);
    }

    public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        HidAttributes attributes = new() { Size = Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle.FileHandle, ref attributes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!HidD_GetPreparsedData(handle.FileHandle, out IntPtr preparsedData))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            HidCaps caps = default;
            int status = HidP_GetCaps(preparsedData, ref caps);
            if (status < 0)
            {
                throw new Win32Exception(status);
            }

            return new HidHandleIdentity(
                attributes.VendorId,
                attributes.ProductId,
                caps.UsagePage,
                caps.Usage,
                caps.FeatureReportByteLength);
        }
        finally
        {
            _ = HidD_FreePreparsedData(preparsedData);
        }
    }

    public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ThreadPoolBoundHandle boundHandle = ThreadPoolBoundHandle.BindHandle(handle.FileHandle);
        SafeFileHandle? transferredFileHandle = null;
        try
        {
            transferredFileHandle = handle.TransferOwnership();
            OpenedHidHandle opened = new(transferredFileHandle, boundHandle);
            transferredFileHandle = null;
            return opened;
        }
        catch
        {
            boundHandle.Dispose();
            transferredFileHandle?.Dispose();
            throw;
        }
    }

    private static bool TryGetDevicePath(
        IntPtr deviceSet,
        ref Guid hidGuid,
        uint index,
        [NotNullWhen(true)] out string? devicePath)
    {
        DeviceInterfaceData interfaceData = new()
        {
            Size = Marshal.SizeOf<DeviceInterfaceData>(),
        };
        if (!SetupDiEnumDeviceInterfaces(
            deviceSet,
            IntPtr.Zero,
            ref hidGuid,
            index,
            ref interfaceData))
        {
            devicePath = null;
            return false;
        }

        _ = SetupDiGetDeviceInterfaceDetail(
            deviceSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);
        IntPtr detail = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                deviceSet,
                ref interfaceData,
                detail,
                requiredSize,
                out _,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            devicePath = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            return !string.IsNullOrWhiteSpace(devicePath);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(
        SafeFileHandle device,
        ref HidAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(
        SafeFileHandle device,
        out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr preparsedData, ref HidCaps capabilities);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

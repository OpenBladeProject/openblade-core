using System.Runtime.InteropServices;

namespace OpenBlade.Display;

internal static class WindowsDisplayNative
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;

    internal const uint QdcAllPaths = 0x00000001;
    internal const uint QdcOnlyActivePaths = 0x00000002;
    internal const uint QdcVirtualModeAware = 0x00000010;
    internal const uint QdcVirtualRefreshRateAware = 0x00000040;

    internal const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    internal const uint SdcValidate = 0x00000040;
    internal const uint SdcApply = 0x00000080;
    internal const uint SdcSaveToDatabase = 0x00000200;
    internal const uint SdcVirtualModeAware = 0x00008000;
    internal const uint SdcVirtualRefreshRateAware = 0x00020000;

    internal const uint DisplayConfigPathActive = 0x00000001;
    internal const uint DisplayConfigPathSupportVirtualMode = 0x00000008;
    internal const uint DisplayConfigPathBoostRefreshRate = 0x00000010;
    internal const uint DisplayConfigOutputTechnologyLvds = 6;
    internal const uint DisplayConfigOutputTechnologyDisplayPortEmbedded = 11;
    internal const uint DisplayConfigOutputTechnologyUdiEmbedded = 13;
    internal const uint DisplayConfigOutputTechnologyInternal = 0x80000000;

    internal const uint DisplayConfigDeviceInfoGetSourceName = 1;
    internal const uint DisplayConfigDeviceInfoGetTargetName = 2;
    internal const uint DisplayConfigDeviceInfoGetAdvancedColorInfo2 = 15;
    internal const uint DisplayConfigDeviceInfoSetHdrState = 16;

    internal const uint EnumCurrentSettings = 0xFFFFFFFF;
    internal const uint DmInterlaced = 0x00000002;
    internal const uint CurrentUserScope = 1;
    internal const uint SystemWideScope = 0;
    internal const uint ClassMonitor = 1;
    internal const uint ColorProfileTypeIcc = 0;
    internal const uint ColorProfileSubtypeStandardDisplay = 7;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DisplayConfigPathInfo[] pathInfoArray,
        uint numModeInfoArrayElements,
        [In] DisplayConfigModeInfo[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetSourceName(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetTargetName(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetAdvancedColorInfo2(ref DisplayConfigGetAdvancedColorInfo2 requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    internal static extern int DisplayConfigSetHdrState(ref DisplayConfigSetHdrState setPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsEx(
        string deviceName,
        uint modeNum,
        ref DevMode devMode,
        uint flags);

    [DllImport("mscms.dll", ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayList(
        uint scope,
        DisplayConfigLuid targetAdapterId,
        uint sourceId,
        out IntPtr profileList,
        out uint profileCount);

    [DllImport("mscms.dll", ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayDefault(
        uint scope,
        DisplayConfigLuid targetAdapterId,
        uint sourceId,
        uint profileType,
        uint profileSubtype,
        out IntPtr profileName);

    [DllImport("mscms.dll", ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayUserScope(
        DisplayConfigLuid targetAdapterId,
        uint sourceId,
        out uint scope);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WcsSetUsePerUserProfiles(
        string deviceName,
        uint deviceClass,
        [MarshalAs(UnmanagedType.Bool)] bool usePerUserProfiles);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileAddDisplayAssociation(
        uint scope,
        string profileName,
        DisplayConfigLuid targetAdapterId,
        uint sourceId,
        int setAsDefault,
        int associateAsAdvancedColor);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileSetDisplayDefaultAssociation(
        uint scope,
        string profileName,
        uint profileType,
        uint profileSubtype,
        DisplayConfigLuid targetAdapterId,
        uint sourceId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr memory);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigLuid : IEquatable<DisplayConfigLuid>
{
    internal uint LowPart;
    internal int HighPart;

    public readonly bool Equals(DisplayConfigLuid other) => LowPart == other.LowPart && HighPart == other.HighPart;

    public override readonly bool Equals(object? obj) => obj is DisplayConfigLuid other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(LowPart, HighPart);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigRational
{
    internal uint Numerator;
    internal uint Denominator;

    internal readonly double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    internal static DisplayConfigRational FromHertz(uint hertz) => new() { Numerator = hertz, Denominator = 1 };
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigPathSourceInfo
{
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIdx;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigPathTargetInfo
{
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIdx;
    internal uint OutputTechnology;
    internal uint Rotation;
    internal uint Scaling;
    internal DisplayConfigRational RefreshRate;
    internal uint ScanLineOrdering;
    internal int TargetAvailable;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigPathInfo
{
    internal DisplayConfigPathSourceInfo SourceInfo;
    internal DisplayConfigPathTargetInfo TargetInfo;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfig2DRegion
{
    internal uint Width;
    internal uint Height;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigVideoSignalInfo
{
    internal ulong PixelRate;
    internal DisplayConfigRational HSyncFreq;
    internal DisplayConfigRational VSyncFreq;
    internal DisplayConfig2DRegion ActiveSize;
    internal DisplayConfig2DRegion TotalSize;
    internal uint VideoStandard;
    internal uint ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigTargetMode
{
    internal DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigPoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigSourceMode
{
    internal uint Width;
    internal uint Height;
    internal uint PixelFormat;
    internal DisplayConfigPoint Position;
}

[StructLayout(LayoutKind.Explicit, Size = 48, Pack = 4)]
internal struct DisplayConfigModeUnion
{
    [FieldOffset(0)] internal DisplayConfigTargetMode TargetMode;
    [FieldOffset(0)] internal DisplayConfigSourceMode SourceMode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigModeInfo
{
    internal uint InfoType;
    internal uint Id;
    internal DisplayConfigLuid AdapterId;
    internal DisplayConfigModeUnion Mode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigDeviceInfoHeader
{
    internal uint Type;
    internal uint Size;
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct DisplayConfigSourceDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string ViewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct DisplayConfigTargetDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Flags;
    internal uint OutputTechnology;
    internal ushort EdidManufactureId;
    internal ushort EdidProductCodeId;
    internal uint ConnectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string MonitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string MonitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigGetAdvancedColorInfo2
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Value;
    internal uint ColorEncoding;
    internal uint BitsPerColorChannel;
    internal uint ActiveColorMode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DisplayConfigSetHdrState
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Value;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DevMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;

    internal ushort SpecVersion;
    internal ushort DriverVersion;
    internal ushort Size;
    internal ushort DriverExtra;
    internal uint Fields;
    internal int PositionX;
    internal int PositionY;
    internal uint DisplayOrientation;
    internal uint DisplayFixedOutput;
    internal short Color;
    internal short Duplex;
    internal short YResolution;
    internal short TTOption;
    internal short Collate;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string FormName;

    internal ushort LogPixels;
    internal uint BitsPerPel;
    internal uint PelsWidth;
    internal uint PelsHeight;
    internal uint DisplayFlags;
    internal uint DisplayFrequency;
    internal uint IcmMethod;
    internal uint IcmIntent;
    internal uint MediaType;
    internal uint DitherType;
    internal uint Reserved1;
    internal uint Reserved2;
    internal uint PanningWidth;
    internal uint PanningHeight;

    internal static DevMode Create() => new()
    {
        DeviceName = string.Empty,
        FormName = string.Empty,
        Size = checked((ushort)Marshal.SizeOf<DevMode>()),
    };
}

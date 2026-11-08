using System.Runtime.InteropServices;

namespace OpenBlade.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOptionalInt
{
    internal int Value;
    internal byte Present;
    internal byte Reserved0;
    internal byte Reserved1;
    internal byte Reserved2;

    internal static NativeOptionalInt From(int? value) => new() { Value = value ?? 0, Present = value.HasValue ? (byte)1 : (byte)0 };
    internal static NativeOptionalInt From(bool? value) => From(value.HasValue ? value.Value ? 1 : 0 : null);
    internal readonly bool IsValid => Present <= 1 && Reserved0 == 0 && Reserved1 == 0 && Reserved2 == 0;
    internal readonly int? OptionalValue => Present == 1 ? Value : null;
    internal readonly bool IsBoolean => IsValid && (Present == 0 || Value is 0 or 1);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOptionalUtf8
{
    internal uint Offset;
    internal uint Length;
    internal byte Present;
    internal byte Reserved0;
    internal byte Reserved1;
    internal byte Reserved2;

    internal readonly bool IsValid => Present <= 1 && Reserved0 == 0 && Reserved1 == 0 && Reserved2 == 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayProjection
{
    internal uint StructSize;
    internal NativeOptionalUtf8 RefreshRateKey;
    internal NativeOptionalInt HdrEnabled;
    internal NativeOptionalUtf8 ColorProfileKey;
    internal NativeOptionalInt BrightnessPercent;

    internal static NativeDisplayProjection Empty => new() { StructSize = 44 };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProfileProjection
{
    internal uint StructSize;
    internal NativeOptionalInt Performance;
    internal NativeOptionalInt Fans;
    internal int FanMode;
    internal int FixedSpeedRpm;
    internal byte HasFanControl;
    internal byte HasDisplay;
    internal byte Reserved0;
    internal byte Reserved1;
    internal NativeOptionalInt GamingModeEnabled;
    internal NativeDisplayProjection Display;
    internal uint FanControlSource;

    internal static NativeProfileProjection Empty => new() { StructSize = 88, Display = NativeDisplayProjection.Empty };
}

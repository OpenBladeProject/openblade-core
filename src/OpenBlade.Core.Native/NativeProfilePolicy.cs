using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Core.Native;

public sealed record NativePolicyLoadResult(NativeProfilePolicy? Policy, NativeBoundaryError? Error)
{
    public bool IsSuccess => Policy is not null && Error is null;
}

public sealed record NativeProfileResult(PowerProfile? Profile, NativeBoundaryError? Error)
{
    public bool IsSuccess => Profile is not null && Error is null;
}

public sealed record NativeDisplayResult(DisplayProfileSettings? Display, NativeBoundaryError? Error)
{
    public bool IsSuccess => Display is not null && Error is null;
}

/// <summary>
/// Binding for pure profile projection. Whole-profile validation, application
/// matching, opaque UTF-16 keys and state ownership stay
/// with the C# host. Failed results do not imply that any hardware command was submitted.
/// </summary>
public sealed unsafe class NativeProfilePolicy : IDisposable
{
    public const uint PolicyAbiVersion = 0x00010000;
    private readonly PolicyModule module;
    private readonly delegate* unmanaged[Cdecl]<uint, NativeProfileProjection*, NativeProfileProjection*, byte*, uint, NativeProfileProjection*, byte*, uint, uint*, NativeErrorRecord*, uint> apply;
    private readonly delegate* unmanaged[Cdecl]<uint, NativeDisplayProjection*, NativeDisplayProjection*, byte*, uint, NativeDisplayProjection*, byte*, uint, uint*, NativeErrorRecord*, uint> applyDisplay;

    private NativeProfilePolicy(PolicyModule module)
    {
        this.module = module;
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            nint handle = module.DangerousGetHandle();
            var policyVersion = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "ob_policy_abi_version");
            if (policyVersion() != PolicyAbiVersion)
            {
                throw new PolicyVersionException();
            }
            apply = (delegate* unmanaged[Cdecl]<uint, NativeProfileProjection*, NativeProfileProjection*, byte*, uint, NativeProfileProjection*, byte*, uint, uint*, NativeErrorRecord*, uint>)NativeLibrary.GetExport(handle, "ob_v1_apply_profile");
            applyDisplay = (delegate* unmanaged[Cdecl]<uint, NativeDisplayProjection*, NativeDisplayProjection*, byte*, uint, NativeDisplayProjection*, byte*, uint, uint*, NativeErrorRecord*, uint>)NativeLibrary.GetExport(handle, "ob_v1_apply_display");
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    public static NativePolicyLoadResult Load(string absoluteLibraryPath)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return new(null, Error(NativeBoundaryErrorKind.UnsupportedPlatform, "The policy helper supports Windows, Linux, or macOS x64/arm64 processes only."));
        }
        if (!LayoutsMatch())
        {
            return new(null, Error(NativeBoundaryErrorKind.InvalidLayout, "Managed policy ABI layout differs from v1."));
        }

        // Policy negotiation is independent of codec versions and codec exports.
        string expectedName = OperatingSystem.IsWindows() ? "openblade_ffi.dll"
            : OperatingSystem.IsMacOS() ? "libopenblade_ffi.dylib" : "libopenblade_ffi.so";
        try
        {
            if (string.IsNullOrWhiteSpace(absoluteLibraryPath)
                || !Path.IsPathFullyQualified(absoluteLibraryPath)
                || !string.Equals(absoluteLibraryPath, Path.GetFullPath(absoluteLibraryPath), StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(absoluteLibraryPath), expectedName, StringComparison.Ordinal)
                || !File.Exists(absoluteLibraryPath))
            {
                return new(null, Error(NativeBoundaryErrorKind.InvalidLibraryPath,
                    $"Supply a normalized absolute path to an existing {expectedName} artifact."));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new(null, Error(NativeBoundaryErrorKind.InvalidLibraryPath, exception.Message));
        }

        PolicyModule? loaded = null;
        try
        {
            loaded = new PolicyModule(NativeLibrary.Load(absoluteLibraryPath));
            return new(new NativeProfilePolicy(loaded), null);
        }
        catch (PolicyVersionException)
        {
            loaded?.Dispose();
            return new(null, Error(NativeBoundaryErrorKind.AbiMismatch, "Native profile policy ABI does not match v1."));
        }
        catch (EntryPointNotFoundException exception)
        {
            loaded?.Dispose();
            return new(null, Error(NativeBoundaryErrorKind.MissingExport, exception.Message));
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
        {
            loaded?.Dispose();
            return new(null, Error(NativeBoundaryErrorKind.LibraryLoadFailure, exception.Message));
        }
    }

    public NativeProfileResult Apply(PowerProfile? baseline, ApplicationPowerProfileOverride? value)
    {
        try
        {
            EnsureAvailable();
            if (baseline is null)
            {
                return new(null, Error(NativeBoundaryErrorKind.InvalidArgument, "A baseline profile is required.", 5));
            }
            if (value is null)
            {
                // Preserve the original no-op branch, including object identity
                // and null fields, without constructing a projection.
                return new(baseline, null);
            }
            var keys = new KeyProjection();
            NativeProfileProjection basis = Project(baseline, keys);
            NativeProfileProjection replacement = Project(value, keys);
            byte[] input = keys.Bytes.ToArray();
            byte[] output = new byte[input.Length];
            NativeProfileProjection result = NativeProfileProjection.Empty;
            NativeErrorRecord error = NativeErrorRecord.Create();
            uint written = 0;
            fixed (byte* inputPointer = input)
            fixed (byte* outputPointer = output)
            {
                uint status = RawApply(PolicyAbiVersion, &basis,
                    &replacement, inputPointer, (uint)input.Length,
                    &result, outputPointer, (uint)output.Length, &written, &error);
                NativeBoundaryError? failure = CheckStatus(status, error, written, output.Length);
                if (failure is not null)
                {
                    return new(null, failure);
                }
            }
            if (!ValidProfile(result))
            {
                return InvalidProfile();
            }

            DisplayProfileSettings? display = result.HasDisplay == 1 ? ReadDisplay(result.Display, output.AsSpan(0, (int)written), keys) : null;
            if (value.Display is null && baseline.Display is not null)
            {
                if (display != baseline.Display)
                {
                    return InvalidProfile();
                }
                display = baseline.Display;
            }
            FanControlSettings? fan = ResolveFan(result, baseline, value);
            if (result.HasDisplay != 1)
            {
                return InvalidProfile();
            }

            LaptopBehaviorProfileSettings behavior = baseline.EffectiveLaptopBehavior;
            if (value.GamingModeEnabled.HasValue || value.GamingModeKeyPolicy is not null)
            {
                behavior = behavior with
                {
                    GamingModeEnabled = ReadBoolean(result.GamingModeEnabled),
                    GamingModeKeyPolicy = value.GamingModeKeyPolicy ?? behavior.GamingModeKeyPolicy,
                };
            }
            else if (ReadBoolean(result.GamingModeEnabled) != behavior.GamingModeEnabled)
            {
                return InvalidProfile();
            }

            return new(baseline with
            {
                Performance = (PerformancePreset)result.Performance.Value,
                Fans = (FanMode)result.Fans.Value,
                FanControl = fan,
                Display = display,
                LaptopBehavior = behavior,
                CustomPerformance = value.CustomPerformance ?? baseline.CustomPerformance,
                GpuClockTuning = value.GpuClockTuning ?? baseline.GpuClockTuning,
                Lighting = value.Lighting ?? baseline.Lighting,
            }, null);
        }
        catch (InvalidPolicyResultException) { return InvalidProfile(); }
        catch (ObjectDisposedException) { return new(null, Error(NativeBoundaryErrorKind.Disposed, "The native policy helper is disposed.")); }
    }

    /// <summary>The bounded display ABI requires a nonnull baseline; Apply supplies an unconfigured fallback.</summary>
    public NativeDisplayResult ApplyDisplay(DisplayProfileSettings? baseline, DisplayProfileSettings? value)
    {
        try
        {
            EnsureAvailable();
            if (baseline is null)
            {
                return new(null, Error(NativeBoundaryErrorKind.InvalidArgument, "A baseline display is required.", 5));
            }
            if (value is null)
            {
                return new(baseline, null);
            }
            var keys = new KeyProjection();
            NativeDisplayProjection basis = ProjectDisplay(baseline, keys);
            NativeDisplayProjection replacement = ProjectDisplay(value, keys);
            byte[] input = keys.Bytes.ToArray();
            byte[] output = new byte[input.Length];
            NativeDisplayProjection result = NativeDisplayProjection.Empty;
            NativeErrorRecord error = NativeErrorRecord.Create();
            uint written = 0;
            fixed (byte* inputPointer = input)
            fixed (byte* outputPointer = output)
            {
                uint status = RawApplyDisplay(PolicyAbiVersion, &basis,
                    &replacement, inputPointer, (uint)input.Length,
                    &result, outputPointer, (uint)output.Length, &written, &error);
                NativeBoundaryError? failure = CheckStatus(status, error, written, output.Length);
                if (failure is not null)
                {
                    return new(null, failure);
                }
            }
            DisplayProfileSettings display = ReadDisplay(result, output.AsSpan(0, (int)written), keys);
            return new(display, null);
        }
        catch (InvalidPolicyResultException) { return new(null, InvalidResult()); }
        catch (ObjectDisposedException) { return new(null, Error(NativeBoundaryErrorKind.Disposed, "The native policy helper is disposed.")); }
    }

    private void EnsureAvailable()
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    internal uint RawApply(uint version, NativeProfileProjection* baseline, NativeProfileProjection* value,
        byte* keys, uint keyLength, NativeProfileProjection* result, byte* output, uint capacity,
        uint* written, NativeErrorRecord* error)
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return apply(version, baseline, value, keys, keyLength, result, output, capacity, written, error);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    internal uint RawApplyDisplay(uint version, NativeDisplayProjection* baseline, NativeDisplayProjection* value,
        byte* keys, uint keyLength, NativeDisplayProjection* result, byte* output, uint capacity,
        uint* written, NativeErrorRecord* error)
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return applyDisplay(version, baseline, value, keys, keyLength, result, output, capacity, written, error);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    private static NativeProfileProjection Project(PowerProfile? profile, KeyProjection keys)
    {
        NativeProfileProjection result = NativeProfileProjection.Empty;
        if (profile is null)
        {
            return result;
        }

        result.Performance = NativeOptionalInt.From((int)profile.Performance);
        result.Fans = NativeOptionalInt.From((int)profile.Fans);
        result.GamingModeEnabled = NativeOptionalInt.From(profile.EffectiveLaptopBehavior.GamingModeEnabled);
        SetObjects(ref result, profile.FanControl, profile.Display, keys);
        return result;
    }

    private static NativeProfileProjection Project(ApplicationPowerProfileOverride? profile, KeyProjection keys)
    {
        NativeProfileProjection result = NativeProfileProjection.Empty;
        if (profile is null)
        {
            return result;
        }

        result.Performance = NativeOptionalInt.From(profile.Performance.HasValue ? (int)profile.Performance.Value : null);
        result.Fans = NativeOptionalInt.From(profile.Fans.HasValue ? (int)profile.Fans.Value : null);
        result.GamingModeEnabled = NativeOptionalInt.From(profile.GamingModeEnabled);
        SetObjects(ref result, profile.FanControl, profile.Display, keys);
        return result;
    }

    private static void SetObjects(ref NativeProfileProjection result, FanControlSettings? fan, DisplayProfileSettings? display, KeyProjection keys)
    {
        result.HasFanControl = fan is null ? (byte)0 : (byte)1;
        result.FanMode = fan is null ? 0 : (int)fan.Mode;
        result.FixedSpeedRpm = fan?.FixedSpeedRpm ?? 0;
        result.HasDisplay = display is null ? (byte)0 : (byte)1;
        result.Display = ProjectDisplay(display, keys);
    }

    private static NativeDisplayProjection ProjectDisplay(DisplayProfileSettings? display, KeyProjection keys)
        => new()
        {
            StructSize = 44,
            RefreshRateKey = AddKey(display?.RefreshRateKey, keys),
            HdrEnabled = NativeOptionalInt.From(display?.HdrEnabled),
            ColorProfileKey = AddKey(display?.ColorProfileKey, keys),
            BrightnessPercent = NativeOptionalInt.From(display?.BrightnessPercent is byte brightness ? (int)brightness : null),
        };

    private static NativeOptionalUtf8 AddKey(string? value, KeyProjection keys) => keys.Add(value);

    // At most four present keys cross a call (two per display). Rust selects
    // opaque one-byte source tokens; the original host strings never cross UTF-8.
    // Occurrences are distinct even for equal text, preserving selected spelling
    // and identity, empty strings, NULs, and unpaired UTF-16 surrogates losslessly.
    private sealed class KeyProjection
    {
        private readonly List<string> originals = [];
        internal List<byte> Bytes { get; } = [];

        internal NativeOptionalUtf8 Add(string? value)
        {
            if (value is null)
            {
                return default;
            }
            if (originals.Count >= 4)
            {
                throw new InvalidPolicyResultException();
            }
            uint offset = (uint)originals.Count;
            originals.Add(value);
            Bytes.Add((byte)('A' + offset));
            return new() { Offset = offset, Length = 1, Present = 1 };
        }

        internal string Resolve(ReadOnlySpan<byte> token)
        {
            if (token.Length != 1)
            {
                throw new InvalidPolicyResultException();
            }
            int index = token[0] - 'A';
            if ((uint)index >= (uint)originals.Count)
            {
                throw new InvalidPolicyResultException();
            }
            return originals[index];
        }
    }

    private static bool ValidProfile(NativeProfileProjection value) => value.StructSize == 88
        && value.Performance.IsValid && value.Performance.Present == 1 && value.Fans.IsValid && value.Fans.Present == 1
        && value.HasFanControl <= 1 && value.HasDisplay <= 1 && value.Reserved0 == 0 && value.Reserved1 == 0
        && value.GamingModeEnabled.IsBoolean && value.FanControlSource <= 3
        && (value.HasFanControl == 1 ? value.FanControlSource != 0 : value.FanControlSource == 0);

    private static FanControlSettings? ResolveFan(NativeProfileProjection result, PowerProfile baseline, ApplicationPowerProfileOverride? value)
    {
        FanControlSettings? selected = result.FanControlSource switch
        {
            0 when baseline.FanControl is null && value?.FanControl is null && value?.Fans is null => null,
            1 when value?.FanControl is null && value?.Fans is null => baseline.FanControl,
            2 => value?.FanControl,
            3 when value?.FanControl is null && value?.Fans is FanMode legacy && Enum.IsDefined(legacy) => FanControlSettings.FromLegacy(legacy),
            _ => throw new InvalidPolicyResultException(),
        };
        if (result.HasFanControl == 1 && (selected is null || (int)selected.Mode != result.FanMode || selected.FixedSpeedRpm != result.FixedSpeedRpm))
        {
            throw new InvalidPolicyResultException();
        }

        return selected;
    }

    private static DisplayProfileSettings ReadDisplay(NativeDisplayProjection value, ReadOnlySpan<byte> keys, KeyProjection projection)
    {
        if (value.StructSize != 44 || !value.HdrEnabled.IsBoolean || !value.BrightnessPercent.IsValid
            || (value.BrightnessPercent.Present == 1 && value.BrightnessPercent.Value is < 0 or > 255))
        {
            throw new InvalidPolicyResultException();
        }

        return new(ReadKey(value.RefreshRateKey, keys, projection), ReadBoolean(value.HdrEnabled), ReadKey(value.ColorProfileKey, keys, projection))
        {
            BrightnessPercent = value.BrightnessPercent.Present == 1 ? (byte)value.BrightnessPercent.Value : null,
        };
    }

    private static string? ReadKey(NativeOptionalUtf8 key, ReadOnlySpan<byte> keys, KeyProjection projection)
    {
        if (!key.IsValid)
        {
            throw new InvalidPolicyResultException();
        }

        if (key.Present == 0)
        {
            return null;
        }

        if (key.Offset > (uint)keys.Length || key.Length > (uint)keys.Length - key.Offset)
        {
            throw new InvalidPolicyResultException();
        }

        return projection.Resolve(keys.Slice((int)key.Offset, (int)key.Length));
    }

    private static bool? ReadBoolean(NativeOptionalInt value) => value.Present == 1 ? value.Value == 1 : null;

    internal static NativeBoundaryError? CheckStatus(uint status, NativeErrorRecord error, uint written, int capacity)
    {
        if (error.StructSize != 16 || error.Category != status || status > 5)
        {
            return InvalidResult();
        }

        if (status == 0)
        {
            return error.Detail == 0 && error.RequiredLength == 0 && written <= (uint)capacity ? null : InvalidResult();
        }

        bool validDetail = status switch { 1 or 3 or 5 => error.Detail == 0, 2 => error.Detail is >= 1 and <= 6, _ => false };
        if (!validDetail || (status == 3 ? error.RequiredLength <= (uint)capacity || written != error.RequiredLength : written != 0 || error.RequiredLength != 0))
        {
            return InvalidResult();
        }

        return new((NativeBoundaryErrorKind)status, error.Detail, error.RequiredLength, $"Native policy failed: category {status}, detail {error.Detail}.");
    }

    internal static bool LayoutsMatch() => sizeof(NativeOptionalInt) == 8 && Marshal.SizeOf<NativeOptionalInt>() == 8
        && Offset<NativeOptionalInt>(nameof(NativeOptionalInt.Value), 0) && Offset<NativeOptionalInt>(nameof(NativeOptionalInt.Present), 4)
        && Offset<NativeOptionalInt>(nameof(NativeOptionalInt.Reserved0), 5) && Offset<NativeOptionalInt>(nameof(NativeOptionalInt.Reserved1), 6) && Offset<NativeOptionalInt>(nameof(NativeOptionalInt.Reserved2), 7)
        && sizeof(NativeOptionalUtf8) == 12 && Marshal.SizeOf<NativeOptionalUtf8>() == 12
        && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Offset), 0) && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Length), 4) && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Present), 8)
        && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Reserved0), 9) && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Reserved1), 10) && Offset<NativeOptionalUtf8>(nameof(NativeOptionalUtf8.Reserved2), 11)
        && sizeof(NativeDisplayProjection) == 44 && Marshal.SizeOf<NativeDisplayProjection>() == 44
        && Offset<NativeDisplayProjection>(nameof(NativeDisplayProjection.StructSize), 0) && Offset<NativeDisplayProjection>(nameof(NativeDisplayProjection.RefreshRateKey), 4)
        && Offset<NativeDisplayProjection>(nameof(NativeDisplayProjection.HdrEnabled), 16) && Offset<NativeDisplayProjection>(nameof(NativeDisplayProjection.ColorProfileKey), 24)
        && Offset<NativeDisplayProjection>(nameof(NativeDisplayProjection.BrightnessPercent), 36)
        && sizeof(NativeProfileProjection) == 88 && Marshal.SizeOf<NativeProfileProjection>() == 88
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.StructSize), 0) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.Performance), 4)
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.Fans), 12) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.FanMode), 20)
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.FixedSpeedRpm), 24) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.HasFanControl), 28)
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.HasDisplay), 29) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.Reserved0), 30)
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.Reserved1), 31) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.GamingModeEnabled), 32)
        && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.Display), 40) && Offset<NativeProfileProjection>(nameof(NativeProfileProjection.FanControlSource), 84);

    private static bool Offset<T>(string field, int expected) where T : struct => Marshal.OffsetOf<T>(field).ToInt64() == expected;
    private static NativeProfileResult InvalidProfile() => new(null, InvalidResult());
    private static NativeBoundaryError InvalidResult() => Error(NativeBoundaryErrorKind.InvalidNativeResult, "Native policy returned inconsistent records or buffers.");
    private static NativeBoundaryError Error(NativeBoundaryErrorKind kind, string message, uint detail = 0) => new(kind, detail, 0, message);
    public void Dispose() => module.Dispose();
    private sealed class PolicyVersionException : Exception;
    private sealed class InvalidPolicyResultException : Exception;
    private sealed class PolicyModule : SafeHandle
    {
        public override bool IsInvalid => handle == nint.Zero;
        internal PolicyModule(nint value) : base(nint.Zero, ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() { NativeLibrary.Free(handle); return true; }
    }
}

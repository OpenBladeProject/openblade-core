using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Core.Native;

public enum AutomationValueOrigin : uint
{
    Profile,
    Confirmed,
    Temporary,
    Persistent,
}

public readonly record struct NativeAutomationSelection(AutomationValueOrigin Origin, SettingValueSource Source);
public sealed record NativeAutomationResult(NativeAutomationSelection? Selection, NativeBoundaryError? Error)
{
    public bool IsSuccess => Selection is not null && Error is null;
}

public sealed record NativeAutomationLoadResult(NativeAutomationPolicy? Policy, NativeBoundaryError? Error)
{
    public bool IsSuccess => Policy is not null && Error is null;
}

/// <summary>Pure source-token selection; host values and mutable state never enter native memory.</summary>
public sealed unsafe class NativeAutomationPolicy : IDisposable
{
    public const uint AutomationAbiVersion = 0x00010000;
    private readonly AutomationModule module;
    private readonly delegate* unmanaged[Cdecl]<uint, NativeAutomationRequest*, NativeAutomationRecord*, uint*, NativeErrorRecord*, uint> select;

    private NativeAutomationPolicy(AutomationModule module)
    {
        this.module = module;
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            nint handle = module.DangerousGetHandle();
            var version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "ob_automation_abi_version");
            if (version() != AutomationAbiVersion)
            {
                throw new AutomationVersionException();
            }
            select = (delegate* unmanaged[Cdecl]<uint, NativeAutomationRequest*, NativeAutomationRecord*, uint*, NativeErrorRecord*, uint>)NativeLibrary.GetExport(handle, "ob_v1_select_automation");
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }
    public static NativeAutomationLoadResult Load(string absoluteLibraryPath)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return new(null, Error(NativeBoundaryErrorKind.UnsupportedPlatform, "The automation helper supports Windows, Linux, or macOS x64/arm64 processes only."));
        }
        if (!LayoutsMatch())
        {
            return new(null, Error(NativeBoundaryErrorKind.InvalidLayout, "Managed automation ABI layout differs from v1."));
        }

        // Automation negotiation is independent of codec/profile versions and exports.
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

        AutomationModule? loaded = null;
        try
        {
            loaded = new AutomationModule(NativeLibrary.Load(absoluteLibraryPath));
            return new(new NativeAutomationPolicy(loaded), null);
        }
        catch (AutomationVersionException)
        {
            loaded?.Dispose();
            return new(null, Error(NativeBoundaryErrorKind.AbiMismatch, "Native automation ABI does not match v1."));
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

    public NativeAutomationResult SelectPerformance(bool hasTemporary, bool hasPersistent, bool fromApplication) =>
        Select(new NativeAutomationRequest
        {
            StructSize = 16,
            Operation = 0,
            HasProfile = 1,
            HasTemporary = Flag(hasTemporary),
            HasPersistent = Flag(hasPersistent),
            FromApplication = Flag(fromApplication),
        });

    public NativeAutomationResult SelectLighting(bool hasProfile, bool hasConfirmed,
        bool hasTemporary, bool hasPersistent, bool externalUnknown, bool fromApplication) =>
        Select(new NativeAutomationRequest
        {
            StructSize = 16,
            Operation = 1,
            HasProfile = Flag(hasProfile),
            HasConfirmed = Flag(hasConfirmed),
            HasTemporary = Flag(hasTemporary),
            HasPersistent = Flag(hasPersistent),
            ExternalUnknown = Flag(externalUnknown),
            FromApplication = Flag(fromApplication),
        });

    private NativeAutomationResult Select(NativeAutomationRequest request)
    {
        try
        {
            NativeAutomationRecord result = new() { StructSize = 12 };
            NativeErrorRecord error = NativeErrorRecord.Create();
            uint written = 0;
            uint status = RawSelect(AutomationAbiVersion, &request, &result, &written, &error);
            if (written != 0)
            {
                return InvalidResult();
            }
            NativeBoundaryError? failure = NativeProfilePolicy.CheckStatus(status, error, written, 0);
            if (failure is not null)
            {
                return new(null, failure);
            }
            if (result.StructSize != 12 || result.Origin > 3 || result.Provenance > 4)
            {
                return InvalidResult();
            }
            AutomationValueOrigin origin = (AutomationValueOrigin)result.Origin;
            SettingValueSource source = (SettingValueSource)result.Provenance;
            bool valid = origin switch
            {
                AutomationValueOrigin.Profile => source == (request.ExternalUnknown == 1
                    ? SettingValueSource.PowerProfile
                    : request.FromApplication == 1 ? SettingValueSource.ApplicationProfile : SettingValueSource.PowerProfile),
                AutomationValueOrigin.Confirmed => request.Operation == 1 && source == SettingValueSource.ConfirmedFirmwareState,
                AutomationValueOrigin.Temporary => request.HasTemporary == 1 && source == SettingValueSource.TemporaryOverride,
                AutomationValueOrigin.Persistent => request.HasPersistent == 1 && source == SettingValueSource.PersistentOverride,
                _ => false,
            };
            return valid ? new(new(origin, source), null) : InvalidResult();
        }
        catch (ObjectDisposedException)
        {
            return new(null, Error(NativeBoundaryErrorKind.Disposed, "Native automation policy is disposed."));
        }
    }

    internal uint RawSelect(uint version, NativeAutomationRequest* request, NativeAutomationRecord* result,
        uint* written, NativeErrorRecord* error)
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return select(version, request, result, written, error);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    internal static bool LayoutsMatch() => sizeof(NativeAutomationRequest) == 16
        && Marshal.SizeOf<NativeAutomationRequest>() == 16
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.StructSize), 0)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.Operation), 4)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.HasProfile), 8)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.HasConfirmed), 9)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.HasTemporary), 10)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.HasPersistent), 11)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.ExternalUnknown), 12)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.FromApplication), 13)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.Reserved0), 14)
        && Offset<NativeAutomationRequest>(nameof(NativeAutomationRequest.Reserved1), 15)
        && sizeof(NativeAutomationRecord) == 12 && Marshal.SizeOf<NativeAutomationRecord>() == 12
        && Offset<NativeAutomationRecord>(nameof(NativeAutomationRecord.StructSize), 0)
        && Offset<NativeAutomationRecord>(nameof(NativeAutomationRecord.Origin), 4)
        && Offset<NativeAutomationRecord>(nameof(NativeAutomationRecord.Provenance), 8);

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;
    private static bool Offset<T>(string field, int expected) where T : struct =>
        Marshal.OffsetOf<T>(field).ToInt64() == expected;
    private static NativeBoundaryError Error(NativeBoundaryErrorKind kind, string message) => new(kind, 0, 0, message);
    private static NativeAutomationResult InvalidResult() => new(null,
        Error(NativeBoundaryErrorKind.InvalidNativeResult, "Native automation policy returned inconsistent records."));
    public void Dispose() => module.Dispose();
    private sealed class AutomationVersionException : Exception;
    private sealed class AutomationModule : SafeHandle
    {
        internal AutomationModule(nint value) : base(nint.Zero, ownsHandle: true) => SetHandle(value);
        public override bool IsInvalid => handle == nint.Zero;
        protected override bool ReleaseHandle() { NativeLibrary.Free(handle); return true; }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAutomationRequest
{
    internal uint StructSize;
    internal uint Operation;
    internal byte HasProfile;
    internal byte HasConfirmed;
    internal byte HasTemporary;
    internal byte HasPersistent;
    internal byte ExternalUnknown;
    internal byte FromApplication;
    internal byte Reserved0;
    internal byte Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAutomationRecord
{
    internal uint StructSize;
    internal uint Origin;
    internal uint Provenance;
}

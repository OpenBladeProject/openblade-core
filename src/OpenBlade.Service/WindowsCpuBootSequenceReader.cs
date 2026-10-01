using System.Runtime.InteropServices;

namespace OpenBlade.Service;

internal readonly record struct WindowsCpuBootSequenceCopy(bool Succeeded, nuint BytesCopied, uint Value);

// Fixed operations only: no caller-supplied address, process or read length.
internal interface IWindowsCpuBootSequenceNative
{
    bool IsNativeWindowsAmd64();
    bool MatchesReviewedWindowsVersion();
    WindowsCpuBootSequenceCopy CopyFixedBootSequence();
}

// Observation only. The loader counter does not establish CPU-state continuity,
// loaded-driver identity, power-transition coverage or restart recovery authority.
// Default construction performs no OS queries or memory reads.
internal sealed class WindowsCpuBootSequenceReader
{
    private readonly IWindowsCpuBootSequenceNative native;

    internal WindowsCpuBootSequenceReader() : this(new Native()) { }
    internal WindowsCpuBootSequenceReader(IWindowsCpuBootSequenceNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
    }

    internal CpuLiveCurveBootSequence? Read(CancellationToken token = default)
    {
        try
        {
            if (token.IsCancellationRequested || !native.IsNativeWindowsAmd64()
                || token.IsCancellationRequested || !native.MatchesReviewedWindowsVersion()
                || token.IsCancellationRequested) { return null; }
            var copy = native.CopyFixedBootSequence();
            return !token.IsCancellationRequested && copy.Succeeded && copy.BytesCopied == sizeof(uint)
                ? new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, copy.Value) : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool IsReviewedWindowsVersion(int status, uint major, uint minor, uint build,
        ushort servicePackMajor, ushort servicePackMinor) => status == 0
        && major == 10 && minor == 0 && build == 26200 && servicePackMajor == 0 && servicePackMinor == 0;

    // Full RTL_OSVERSIONINFOEXW from SDK winnt.h, including the numeric service
    // pack fields. RtlGetVersion selects the EX contract from its 284-byte size.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OsVersionInfo
    {
        public uint Size, Major, Minor, Build, PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        public byte ProductType, Reserved;
    }

    private sealed class Native : IWindowsCpuBootSequenceNative
    {
        // SDK 10.0.26100.0 ntddk.h: compiled FIELD_OFFSET(BootId)=0x2C4,
        // sizeof(BootId)=4, sizeof(KUSER_SHARED_DATA)=0xA80. ksamd64.inc
        // supplies user mapping 0x7FFE0000. Reviewed read-only acquisition used
        // this exact layout on native AMD64 with the 10.0.26200 predicate.
        private const int BootSequenceAddress = 0x7FFE02C4;
        private const ushort MachineUnknown = 0;
        private const ushort MachineAmd64 = 0x8664;

        public bool IsNativeWindowsAmd64() => OperatingSystem.IsWindows()
            && IntPtr.Size == 8 && RuntimeInformation.ProcessArchitecture == Architecture.X64
            && IsWow64Process2(GetCurrentProcess(), out ushort processMachine, out ushort nativeMachine)
            && processMachine == MachineUnknown && nativeMachine == MachineAmd64;

        public bool MatchesReviewedWindowsVersion()
        {
            var version = new OsVersionInfo
            {
                Size = (uint)Marshal.SizeOf<OsVersionInfo>(),
                ServicePack = string.Empty,
            };
            // The documented user-mode ntdll query does not depend on the host's
            // supportedOS manifest. Keep the same five exact numeric conditions;
            // no fallback or widening to adjacent/future builds is allowed.
            int status = RtlGetVersion(ref version);
            return IsReviewedWindowsVersion(status, version.Major, version.Minor, version.Build,
                version.ServicePackMajor, version.ServicePackMinor);
        }

        public WindowsCpuBootSequenceCopy CopyFixedBootSequence()
        {
            // The current-process pseudo handle needs no open/close or elevated
            // privilege. The diagnostic's medium-token restriction is not a
            // memory-read requirement and must not exclude the Service account.
            bool succeeded = ReadProcessMemory(GetCurrentProcess(), (nint)BootSequenceAddress,
                out uint value, sizeof(uint), out nuint copied);
            return new(succeeded, copied, value);
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint GetCurrentProcess();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ntdll.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo version);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, out uint value, nuint size, out nuint copied);
    }
}

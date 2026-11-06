using System.Runtime.InteropServices;

namespace OpenBlade.Capture;

internal sealed record So6120InpOutNativeResult(bool Succeeded, uint BytesReturned, int NativeError);

// Injection seam only, not an application raw-port API. The native implementation
// is private; the fixed attended Windows platform owns live handle acquisition.
internal interface ISo6120InpOutNative
{
    So6120InpOutNativeResult Invoke(SafeHandle handle, uint controlCode, byte[] input, byte[]? output);
}

// Source: docs/so6120-host-mailbox-contract.md, exact x64 driver SHA256
// f8965fdce668692c3785afa3559159f9a18287bc0d53abb21902895a8ecf221b.
// Reviewed instructions: RVA134C..1365 read, 136A..1383 write (Information=10).
// The native listing SHA256 is
// 0310f840b9b7c60e0f8b85f03767839afeb18c0704faefe0512bbdb5091fa6bf.
// This is a byte-I/O adapter, NOT mailbox exclusion, admission or a fan lease.
// Its lock protects this handle's lifetime only. It cannot protect an index/data
// pair or transaction against other handles, firmware or reentrant callers.
internal sealed class So6120InpOutBytePort : ISo6120MailboxBytePort, IDisposable
{
    private const uint ReadByteControl = 0x9C402004;
    private const uint WriteByteControl = 0x9C402008;
    private readonly object lifetime = new();
    private readonly SafeHandle handle;
    private readonly ISo6120InpOutNative native;
    private bool disposed;

    // Ownership transfers only after successful construction. The caller must
    // not reuse/dispose the handle after transfer. This injectable constructor
    // does not assert that a supplied handle has passed device/driver admission.
    internal So6120InpOutBytePort(SafeHandle ownedHandle, ISo6120InpOutNative native)
    {
        ArgumentNullException.ThrowIfNull(ownedHandle);
        ArgumentNullException.ThrowIfNull(native);
        if (ownedHandle.IsClosed || ownedHandle.IsInvalid)
        {
            throw new ArgumentException("A valid owned handle is required.", nameof(ownedHandle));
        }
        handle = ownedHandle;
        this.native = native;
    }

    // The parameterless route has no attended consent or driver binding.
    public static So6120InpOutBytePort Acquire() =>
        throw new NotSupportedException("Use the attended SO6120 platform acquisition.");

    // Only the fixed attended platform passes its owned device handle here.
    // The platform retains the driver-file pin and runner ownership.
    internal static So6120InpOutBytePort AttachWindowsHandle(So6120OwnedInpOutHandle ownedHandle) =>
        new(ownedHandle, new NativeCalls());

    public So6120PortResult SelectIndex(byte index)
    {
        // Only the one-byte mailbox fields used by the transaction core.
        if (index is not (0x90 or 0x91 or 0x92 or 0x93 or 0x94 or 0xA0))
        {
            return new(false, 0, 0, 87, So6120PortEffect.NotPerformed);
        }
        return Exchange(WriteByteControl, [0x00, 0x03, index], null);
    }

    public So6120PortResult ReadData() => Exchange(ReadByteControl, [0x01, 0x03], new byte[1]);

    public So6120PortResult WriteData(byte value) => Exchange(WriteByteControl, [0x01, 0x03, value], null);

    private So6120PortResult Exchange(uint controlCode, byte[] input, byte[]? output)
    {
        lock (lifetime)
        {
            if (disposed || handle.IsClosed || handle.IsInvalid)
            {
                return new(false, 0, 0, 6, So6120PortEffect.NotPerformed);
            }
            bool retained = false;
            try
            {
                // Protect the native call even if the original owner violates
                // transfer ownership and disposes its retained handle reference.
                handle.DangerousAddRef(ref retained);
            }
            catch (ObjectDisposedException)
            {
                return new(false, 0, 0, 6, So6120PortEffect.NotPerformed);
            }
            try
            {
                // Exceptions deliberately propagate to the core's operation
                // ledger: once entered, the boundary cannot promise no I/O.
                // No retry, alternate IOCTL or recovery write is attempted.
                So6120InpOutNativeResult result = native.Invoke(handle, controlCode, input, output);
                bool validRead = output is null || result.BytesReturned == 1;
                bool usable = result.Succeeded && validRead;
                return new(result.Succeeded, result.BytesReturned,
                    usable && output is not null ? output[0] : (byte)0,
                    result.NativeError,
                    usable ? So6120PortEffect.Performed : So6120PortEffect.MayHavePerformed);
            }
            finally
            {
                if (retained) { handle.DangerousRelease(); }
            }
        }
    }

    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) { return; }
            disposed = true;
            handle.Dispose();
        }
    }

    // The transaction's host deadline cannot interrupt a synchronous native
    // call. The attended supervisor accounts for late or stalled calls.
    private sealed class NativeCalls : ISo6120InpOutNative
    {
        public So6120InpOutNativeResult Invoke(SafeHandle handle, uint controlCode, byte[] input, byte[]? output)
        {
            bool success = DeviceIoControl(handle, controlCode, input, (uint)input.Length,
                output, (uint)(output?.Length ?? 0), out uint returned, IntPtr.Zero);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            return new(success, returned, error);
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeHandle device, uint controlCode,
            [In] byte[] input, uint inputLength, [Out] byte[]? output, uint outputLength,
            out uint bytesReturned, IntPtr overlapped);
    }
}

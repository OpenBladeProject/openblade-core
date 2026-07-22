using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OpenBlade.SessionAgent;

public interface IUserDisplayStateSource
{
    IAsyncEnumerable<bool> WatchDisplayOnAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes the display associated with the signed-in user session. Windows does
/// not deliver GUID_SESSION_DISPLAY_STATUS to services running in session 0, so
/// this source belongs in SessionAgent beside the user-idle observer.
/// </summary>
public sealed class WindowsSessionDisplayStateSource : IUserDisplayStateSource
{
    private const uint PowerMonitorOff = 0;
    private const uint PowerMonitorOn = 1;
    private const uint PowerMonitorDimmed = 2;

    internal static bool? ClassifyDisplayOn(uint value) => value switch
    {
        PowerMonitorOff => false,
        PowerMonitorOn or PowerMonitorDimmed => true,
        _ => null,
    };

    public async IAsyncEnumerable<bool> WatchDisplayOnAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Session display notifications require Windows.");
        }

        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var subscription = new NativeSubscription(
            displayOn => updates.Writer.TryWrite(displayOn));
        try
        {
            await foreach (bool displayOn in updates.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return displayOn;
            }
        }
        finally
        {
            updates.Writer.TryComplete();
        }
    }

    private sealed class NativeSubscription : IDisposable
    {
        private const uint DeviceNotifyCallbackFlag = 2;
        private static readonly Guid SessionDisplayStatus =
            new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

        private readonly Action<bool> publish;
        private readonly DeviceNotifyCallback callback;
        private IntPtr registration;
        private int lastDisplayOn = -1;
        private int disposed;

        public NativeSubscription(Action<bool> publish)
        {
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
            callback = OnNotification;
            var recipient = new DeviceNotifySubscribeParameters
            {
                Callback = callback,
                Context = IntPtr.Zero,
            };
            Guid setting = SessionDisplayStatus;
            uint result = PowerSettingRegisterNotification(
                ref setting,
                DeviceNotifyCallbackFlag,
                ref recipient,
                out registration);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            IntPtr handle = Interlocked.Exchange(ref registration, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            uint result = PowerSettingUnregisterNotification(handle);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }
        }

        private uint OnNotification(IntPtr context, uint type, IntPtr setting)
        {
            if (Volatile.Read(ref disposed) != 0
                || !TryReadPowerSettingDword(setting, out uint current)
                || ClassifyDisplayOn(current) is not bool displayOn)
            {
                return 0;
            }

            int encoded = displayOn ? 1 : 0;
            if (Interlocked.Exchange(ref lastDisplayOn, encoded) != encoded)
            {
                publish(displayOn);
            }
            return 0;
        }

        private static bool TryReadPowerSettingDword(IntPtr setting, out uint value)
        {
            value = 0;
            if (setting == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var header = Marshal.PtrToStructure<PowerBroadcastSettingHeader>(setting);
                if (header.PowerSetting != SessionDisplayStatus
                    || header.DataLength < sizeof(uint))
                {
                    return false;
                }

                value = unchecked((uint)Marshal.ReadInt32(
                    setting,
                    Marshal.SizeOf<PowerBroadcastSettingHeader>()));
                return true;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNotifySubscribeParameters
        {
            public DeviceNotifyCallback Callback;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerBroadcastSettingHeader
        {
            public Guid PowerSetting;
            public uint DataLength;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint DeviceNotifyCallback(
            IntPtr context,
            uint type,
            IntPtr setting);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerSettingRegisterNotification(
            ref Guid settingGuid,
            uint flags,
            ref DeviceNotifySubscribeParameters recipient,
            out IntPtr registrationHandle);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerSettingUnregisterNotification(
            IntPtr registrationHandle);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OpenSynapse.Service;

public enum ServiceLifecycleTrigger
{
    PowerChanged,
    Resumed,
    DeviceArrived,
}

public interface IServiceLifecycleEventSource
{
    IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
        CancellationToken cancellationToken);
}

public interface IWindowsLifecycleNotificationBackend
{
    IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish);
}

public sealed class WindowsServiceLifecycleEventSource(
    IWindowsLifecycleNotificationBackend backend) : IServiceLifecycleEventSource
{
    public WindowsServiceLifecycleEventSource()
        : this(new NativeWindowsLifecycleNotificationBackend())
    {
    }

    public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var notifications = Channel.CreateBounded<ServiceLifecycleTrigger>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        using IDisposable subscription = backend.Subscribe(
            trigger => notifications.Writer.TryWrite(trigger));

        await foreach (ServiceLifecycleTrigger trigger in
            notifications.Reader.ReadAllAsync(cancellationToken))
        {
            yield return trigger;
        }
    }
}

internal sealed class NativeWindowsLifecycleNotificationBackend
    : IWindowsLifecycleNotificationBackend
{
    public IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows lifecycle notifications require Windows.");
        }

        return new NativeSubscription(publish);
    }

    private sealed class NativeSubscription : IDisposable
    {
        private const uint DeviceNotifyCallbackFlag = 2;
        private const uint PowerResumeAutomatic = 0x0012;
        private const uint PowerResumeSuspend = 0x0007;
        private const uint DeviceInterfaceArrival = 0;
        private const uint CrSuccess = 0;

        private static readonly Guid AcDcPowerSource =
            new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
        private static readonly Guid HidDeviceInterface =
            new("4D1E55B2-F16F-11CF-88CB-001111000030");

        private readonly Action<ServiceLifecycleTrigger> publish;
        private readonly DeviceNotifyCallback suspendResumeCallback;
        private readonly DeviceNotifyCallback powerSourceCallback;
        private readonly CmNotifyCallback deviceCallback;
        private IntPtr suspendResumeRegistration;
        private IntPtr powerSourceRegistration;
        private IntPtr deviceRegistration;
        private int disposed;

        public NativeSubscription(Action<ServiceLifecycleTrigger> publish)
        {
            this.publish = publish;
            suspendResumeCallback = OnSuspendResumeNotification;
            powerSourceCallback = OnPowerSourceNotification;
            deviceCallback = OnDeviceNotification;

            try
            {
                var recipient = new DeviceNotifySubscribeParameters
                {
                    Callback = suspendResumeCallback,
                    Context = IntPtr.Zero,
                };
                ThrowIfWin32Error(PowerRegisterSuspendResumeNotification(
                    DeviceNotifyCallbackFlag,
                    ref recipient,
                    out suspendResumeRegistration));

                Guid powerSource = AcDcPowerSource;
                recipient.Callback = powerSourceCallback;
                ThrowIfWin32Error(PowerSettingRegisterNotification(
                    ref powerSource,
                    DeviceNotifyCallbackFlag,
                    ref recipient,
                    out powerSourceRegistration));

                var filter = new CmNotifyFilter
                {
                    Size = (uint)Marshal.SizeOf<CmNotifyFilter>(),
                    FilterType = 0,
                    ClassGuid = HidDeviceInterface,
                };
                uint configurationResult = CmRegisterNotification(
                    ref filter,
                    IntPtr.Zero,
                    deviceCallback,
                    out deviceRegistration);
                if (configurationResult != CrSuccess)
                {
                    throw new InvalidOperationException(
                        $"CM_Register_Notification failed with CONFIGRET 0x{configurationResult:X8}.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            if (deviceRegistration != IntPtr.Zero)
            {
                _ = CmUnregisterNotification(deviceRegistration);
                deviceRegistration = IntPtr.Zero;
            }

            if (powerSourceRegistration != IntPtr.Zero)
            {
                _ = PowerSettingUnregisterNotification(powerSourceRegistration);
                powerSourceRegistration = IntPtr.Zero;
            }

            if (suspendResumeRegistration != IntPtr.Zero)
            {
                _ = PowerUnregisterSuspendResumeNotification(suspendResumeRegistration);
                suspendResumeRegistration = IntPtr.Zero;
            }
        }

        private uint OnSuspendResumeNotification(IntPtr context, uint type, IntPtr setting)
        {
            if (type is PowerResumeAutomatic or PowerResumeSuspend
                && Volatile.Read(ref disposed) == 0)
            {
                publish(ServiceLifecycleTrigger.Resumed);
            }

            return 0;
        }

        private uint OnPowerSourceNotification(IntPtr context, uint type, IntPtr setting)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                publish(ServiceLifecycleTrigger.PowerChanged);
            }

            return 0;
        }

        private uint OnDeviceNotification(
            IntPtr notification,
            IntPtr context,
            uint action,
            IntPtr eventData,
            uint eventDataSize)
        {
            if (action == DeviceInterfaceArrival && Volatile.Read(ref disposed) == 0)
            {
                publish(ServiceLifecycleTrigger.DeviceArrived);
            }

            return 0;
        }

        private static void ThrowIfWin32Error(uint error)
        {
            if (error != 0)
            {
                throw new Win32Exception((int)error);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNotifySubscribeParameters
        {
            public DeviceNotifyCallback Callback;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Explicit, Size = 416)]
        private struct CmNotifyFilter
        {
            [FieldOffset(0)] public uint Size;
            [FieldOffset(4)] public uint Flags;
            [FieldOffset(8)] public uint FilterType;
            [FieldOffset(12)] public uint Reserved;
            [FieldOffset(16)] public Guid ClassGuid;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint DeviceNotifyCallback(
            IntPtr context,
            uint type,
            IntPtr setting);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint CmNotifyCallback(
            IntPtr notification,
            IntPtr context,
            uint action,
            IntPtr eventData,
            uint eventDataSize);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerRegisterSuspendResumeNotification(
            uint flags,
            ref DeviceNotifySubscribeParameters recipient,
            out IntPtr registrationHandle);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerUnregisterSuspendResumeNotification(
            IntPtr registrationHandle);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerSettingRegisterNotification(
            ref Guid settingGuid,
            uint flags,
            ref DeviceNotifySubscribeParameters recipient,
            out IntPtr registrationHandle);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerSettingUnregisterNotification(
            IntPtr registrationHandle);

        [DllImport("CfgMgr32.dll", EntryPoint = "CM_Register_Notification")]
        private static extern uint CmRegisterNotification(
            ref CmNotifyFilter filter,
            IntPtr context,
            CmNotifyCallback callback,
            out IntPtr notification);

        [DllImport("CfgMgr32.dll", EntryPoint = "CM_Unregister_Notification")]
        private static extern uint CmUnregisterNotification(IntPtr notification);
    }
}

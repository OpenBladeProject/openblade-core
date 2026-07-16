using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OpenBlade.Service;

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

public interface IWindowsLifecycleUnregistrationApi
{
    uint UnregisterDevice(IntPtr registration);

    uint UnregisterPowerSource(IntPtr registration);

    uint UnregisterSuspendResume(IntPtr registration);
}

public sealed class WindowsLifecycleRegistrationCleanup(
    IWindowsLifecycleUnregistrationApi api,
    IntPtr deviceRegistration,
    IntPtr powerSourceRegistration,
    IntPtr suspendResumeRegistration,
    object callbackLifetime) : IDisposable
{
    private static readonly ConcurrentBag<WindowsLifecycleRegistrationCleanup>
        FailedCleanups = [];

    private readonly object sync = new();
    private object? callbackLifetime = callbackLifetime;
    private bool attempted;
    private AggregateException? failure;

    public void Dispose()
    {
        lock (sync)
        {
            if (attempted)
            {
                if (failure is not null)
                {
                    throw failure;
                }

                return;
            }

            attempted = true;
            List<Exception> failures = [];

            if (deviceRegistration != IntPtr.Zero)
            {
                uint result = api.UnregisterDevice(deviceRegistration);
                if (result == 0)
                {
                    deviceRegistration = IntPtr.Zero;
                }
                else
                {
                    failures.Add(new InvalidOperationException(
                        $"CM_Unregister_Notification failed with CONFIGRET 0x{result:X8}."));
                }
            }

            if (powerSourceRegistration != IntPtr.Zero)
            {
                uint result = api.UnregisterPowerSource(powerSourceRegistration);
                if (result == 0)
                {
                    powerSourceRegistration = IntPtr.Zero;
                }
                else
                {
                    failures.Add(new Win32Exception((int)result));
                }
            }

            if (suspendResumeRegistration != IntPtr.Zero)
            {
                uint result = api.UnregisterSuspendResume(suspendResumeRegistration);
                if (result == 0)
                {
                    suspendResumeRegistration = IntPtr.Zero;
                }
                else
                {
                    failures.Add(new Win32Exception((int)result));
                }
            }

            if (failures.Count > 0)
            {
                failure = new AggregateException(
                    "One or more Windows lifecycle notifications could not be unregistered.",
                    failures);
                FailedCleanups.Add(this);
                throw failure;
            }

            callbackLifetime = null;
        }
    }
}

public sealed class WindowsServiceLifecycleEventSource
    : IServiceLifecycleEventSource, IDisposable
{
    private const int PowerChangedBit = 1 << 0;
    private const int ResumedBit = 1 << 1;
    private const int DeviceArrivedBit = 1 << 2;
    private static readonly ConcurrentBag<IDisposable> FailedSubscriptions = [];
    private readonly object disposeSync = new();
    private readonly Channel<bool> signal;
    private readonly IDisposable subscription;
    private Exception? disposalFailure;
    private int acceptingCallbacks = 1;
    private int disposed;
    private int pendingTriggers;
    private int watcherStarted;

    public WindowsServiceLifecycleEventSource()
        : this(new NativeWindowsLifecycleNotificationBackend())
    {
    }

    public WindowsServiceLifecycleEventSource(IWindowsLifecycleNotificationBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        subscription = backend.Subscribe(Publish);
    }

    public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref watcherStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "Only one Windows lifecycle watcher may be active.");
        }

        while (await signal.Reader.WaitToReadAsync(cancellationToken))
        {
            while (signal.Reader.TryRead(out _))
            {
            }

            while (true)
            {
                int drained = Interlocked.Exchange(ref pendingTriggers, 0);
                if (drained == 0)
                {
                    break;
                }

                if ((drained & PowerChangedBit) != 0)
                {
                    yield return ServiceLifecycleTrigger.PowerChanged;
                }

                if ((drained & ResumedBit) != 0)
                {
                    yield return ServiceLifecycleTrigger.Resumed;
                }

                if ((drained & DeviceArrivedBit) != 0)
                {
                    yield return ServiceLifecycleTrigger.DeviceArrived;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (disposeSync)
        {
            if (disposed != 0)
            {
                if (disposalFailure is not null)
                {
                    throw disposalFailure;
                }

                return;
            }

            disposed = 1;
            Volatile.Write(ref acceptingCallbacks, 0);
            try
            {
                subscription.Dispose();
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
                FailedSubscriptions.Add(subscription);
                throw;
            }
            finally
            {
                signal.Writer.TryComplete();
            }
        }
    }

    private void Publish(ServiceLifecycleTrigger trigger)
    {
        if (Volatile.Read(ref acceptingCallbacks) == 0)
        {
            return;
        }

        int bit = trigger switch
        {
            ServiceLifecycleTrigger.PowerChanged => PowerChangedBit,
            ServiceLifecycleTrigger.Resumed => ResumedBit,
            ServiceLifecycleTrigger.DeviceArrived => DeviceArrivedBit,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        };
        Interlocked.Or(ref pendingTriggers, bit);
        signal.Writer.TryWrite(true);
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
        private readonly WindowsLifecycleRegistrationCleanup cleanup;
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

                cleanup = new WindowsLifecycleRegistrationCleanup(
                    NativeUnregistrationApi.Instance,
                    deviceRegistration,
                    powerSourceRegistration,
                    suspendResumeRegistration,
                    this);
            }
            catch (Exception registrationFailure)
            {
                var partialCleanup = new WindowsLifecycleRegistrationCleanup(
                    NativeUnregistrationApi.Instance,
                    deviceRegistration,
                    powerSourceRegistration,
                    suspendResumeRegistration,
                    this);
                try
                {
                    partialCleanup.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(registrationFailure, cleanupFailure);
                }

                throw;
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref disposed, 1);
            cleanup.Dispose();
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

        private sealed class NativeUnregistrationApi : IWindowsLifecycleUnregistrationApi
        {
            public static NativeUnregistrationApi Instance { get; } = new();

            public uint UnregisterDevice(IntPtr registration) =>
                CmUnregisterNotification(registration);

            public uint UnregisterPowerSource(IntPtr registration) =>
                PowerSettingUnregisterNotification(registration);

            public uint UnregisterSuspendResume(IntPtr registration) =>
                PowerUnregisterSuspendResumeNotification(registration);
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

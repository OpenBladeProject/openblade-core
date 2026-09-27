using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBlade.SessionAgent;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint SessionPowerNotificationCallback(IntPtr context, uint type, IntPtr setting);

internal interface ISessionPowerNotificationApi
{
    uint Register(SessionPowerNotificationCallback callback, out IntPtr registration);
    uint Unregister(IntPtr registration);
}

internal sealed class WindowsSessionPowerNotificationRegistration : IDisposable
{
    private static readonly ConcurrentBag<WindowsSessionPowerNotificationRegistration> FailedCleanups = [];
    private readonly SessionPowerTransitionRelay relay;
    private readonly Action? invalidateInput;
    private readonly ISessionPowerNotificationApi api;
    private readonly SessionPowerNotificationCallback callback;
    private IntPtr registration;
    private int disposed;

    internal static WindowsSessionPowerNotificationRegistration? TryCreate(
        SessionPowerTransitionRelay relay,
        ISessionPowerNotificationApi? api = null,
        Action? invalidateInput = null)
    {
        try
        {
            return new WindowsSessionPowerNotificationRegistration(relay, api, invalidateInput);
        }
        catch (Exception exception) when (exception is Win32Exception
            or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning("OpenBlade session power notifications are unavailable: {0} (0x{1:X8}).",
                exception.GetType().Name, exception.HResult);
            return null;
        }
    }

    internal WindowsSessionPowerNotificationRegistration(
        SessionPowerTransitionRelay relay,
        ISessionPowerNotificationApi? api = null,
        Action? invalidateInput = null)
    {
        this.relay = relay;
        this.invalidateInput = invalidateInput;
        this.api = api ?? NativeApi.Instance;
        callback = OnNotification;
        try
        {
            uint result = this.api.Register(callback, out registration);
            if (result != 0)
            {
                throw new Win32Exception(unchecked((int)result));
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
        relay.Dispose();
        try
        {
            if (registration != IntPtr.Zero)
            {
                uint result = api.Unregister(registration);
                if (result != 0)
                {
                    throw new Win32Exception(unchecked((int)result));
                }
                registration = IntPtr.Zero;
            }
            GC.KeepAlive(callback);
        }
        catch (Exception exception)
        {
            // Windows still owns the callback pointer on unregister failure.
            // Root its complete lifetime while suppressing further dispatch.
            FailedCleanups.Add(this);
            Trace.TraceWarning("OpenBlade session power notification unregister failed: {0} (0x{1:X8}).",
                exception.GetType().Name, exception.HResult);
        }
    }

    private uint OnNotification(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                if (type is 4 or 7 or 18)
                {
                    invalidateInput?.Invoke();
                }
                relay.OnPowerNotification(type);
            }
        }
        catch (Exception exception)
        {
            // Never unwind a managed exception through the native callback.
            try
            {
                Trace.TraceWarning("OpenBlade session power notification failed: {0} (0x{1:X8}).",
                    exception.GetType().Name, exception.HResult);
            }
            catch
            {
            }
        }
        return 0;
    }

    private sealed class NativeApi : ISessionPowerNotificationApi
    {
        private const uint DeviceNotifyCallbackFlag = 2;
        internal static NativeApi Instance { get; } = new();

        public uint Register(SessionPowerNotificationCallback callback, out IntPtr registration)
        {
            var recipient = new DeviceNotifySubscribeParameters
            {
                Callback = callback,
                Context = IntPtr.Zero,
            };
            return PowerRegisterSuspendResumeNotification(DeviceNotifyCallbackFlag, ref recipient, out registration);
        }

        public uint Unregister(IntPtr registration) => PowerUnregisterSuspendResumeNotification(registration);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNotifySubscribeParameters
        {
            public SessionPowerNotificationCallback Callback;
            public IntPtr Context;
        }

        [DllImport("PowrProf.dll")]
        private static extern uint PowerRegisterSuspendResumeNotification(
            uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
    }
}

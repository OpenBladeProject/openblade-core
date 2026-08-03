using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OpenBlade.SessionAgent;

public interface IPlaybackMuteStateSource
{
    IAsyncEnumerable<bool?> WatchMutedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes the default Windows playback endpoint through Core Audio callbacks.
/// Volume notifications deliver mute changes immediately, while endpoint
/// notifications move the subscription when the default output changes.
/// </summary>
public sealed class WindowsPlaybackMuteStateSource : IPlaybackMuteStateSource
{
    public async IAsyncEnumerable<bool?> WatchMutedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Playback mute notifications require Windows.");
        }

        var updates = Channel.CreateBounded<bool?>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var subscription = new NativeSubscription(
            muted => updates.Writer.TryWrite(muted));
        try
        {
            await foreach (bool? muted in updates.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return muted;
            }
        }
        finally
        {
            updates.Writer.TryComplete();
        }
    }

    private sealed class NativeSubscription : IDisposable
    {
        private const int ElementNotFoundHResult = unchecked((int)0x80070490);
        private const uint ClassContextAll = 23;

        private readonly object sync = new();
        private readonly Action<bool?> publish;
        private readonly object enumeratorObject;
        private readonly IMMDeviceEnumerator enumerator;
        private readonly DeviceNotificationClient deviceNotification;
        private readonly AudioEndpointVolumeCallback volumeNotification;
        private object? deviceObject;
        private object? endpointObject;
        private IAudioEndpointVolume? endpointVolume;
        private int disposed;

        public NativeSubscription(Action<bool?> publish)
        {
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
            enumeratorObject = CoreAudioDeviceEnumerator.Create();
            enumerator = (IMMDeviceEnumerator)enumeratorObject;
            deviceNotification = new DeviceNotificationClient(this);
            volumeNotification = new AudioEndpointVolumeCallback(this);
            Marshal.ThrowExceptionForHR(
                enumerator.RegisterEndpointNotificationCallback(deviceNotification));
            try
            {
                BindDefaultEndpoint();
            }
            catch
            {
                _ = enumerator.UnregisterEndpointNotificationCallback(
                    deviceNotification);
                ReleaseCom(enumeratorObject);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            object? oldDevice;
            object? oldEndpoint;
            IAudioEndpointVolume? oldVolume;
            lock (sync)
            {
                oldDevice = deviceObject;
                oldEndpoint = endpointObject;
                oldVolume = endpointVolume;
                deviceObject = null;
                endpointObject = null;
                endpointVolume = null;
            }

            if (oldVolume is not null)
            {
                _ = oldVolume.UnregisterControlChangeNotify(volumeNotification);
            }
            _ = enumerator.UnregisterEndpointNotificationCallback(deviceNotification);
            ReleaseCom(oldEndpoint);
            ReleaseCom(oldDevice);
            ReleaseCom(enumeratorObject);
        }

        private void BindDefaultEndpoint()
        {
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                object? nextDeviceObject = null;
                object? nextEndpointObject = null;
                IAudioEndpointVolume? nextVolume = null;
                try
                {
                    int result = enumerator.GetDefaultAudioEndpoint(
                        EDataFlow.Render,
                        ERole.Console,
                        out IMMDevice device);
                    if (result == ElementNotFoundHResult)
                    {
                        ReplaceEndpoint(null, null, null);
                        publish(null);
                        return;
                    }

                    Marshal.ThrowExceptionForHR(result);
                    nextDeviceObject = device;
                    Guid endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(
                        ref endpointVolumeId,
                        ClassContextAll,
                        0,
                        out object endpoint));
                    nextEndpointObject = endpoint;
                    nextVolume = (IAudioEndpointVolume)endpoint;
                    Marshal.ThrowExceptionForHR(
                        nextVolume.RegisterControlChangeNotify(volumeNotification));
                    Marshal.ThrowExceptionForHR(nextVolume.GetMute(out bool muted));

                    ReplaceEndpoint(
                        nextDeviceObject,
                        nextEndpointObject,
                        nextVolume);
                    nextDeviceObject = null;
                    nextEndpointObject = null;
                    nextVolume = null;
                    publish(muted);
                }
                finally
                {
                    if (nextVolume is not null)
                    {
                        _ = nextVolume.UnregisterControlChangeNotify(
                            volumeNotification);
                    }
                    ReleaseCom(nextEndpointObject);
                    ReleaseCom(nextDeviceObject);
                }
            }
        }

        private void ReplaceEndpoint(
            object? nextDevice,
            object? nextEndpoint,
            IAudioEndpointVolume? nextVolume)
        {
            IAudioEndpointVolume? previousVolume = endpointVolume;
            object? previousEndpoint = endpointObject;
            object? previousDevice = deviceObject;
            endpointVolume = nextVolume;
            endpointObject = nextEndpoint;
            deviceObject = nextDevice;
            if (previousVolume is not null)
            {
                _ = previousVolume.UnregisterControlChangeNotify(volumeNotification);
            }
            ReleaseCom(previousEndpoint);
            ReleaseCom(previousDevice);
        }

        private void OnDefaultDeviceChanged(EDataFlow flow, ERole role)
        {
            if (flow != EDataFlow.Render
                || role != ERole.Console
                || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                BindDefaultEndpoint();
            }
            catch
            {
                lock (sync)
                {
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        ReplaceEndpoint(null, null, null);
                    }
                }
                publish(null);
            }
        }

        private void OnVolumeChanged(IntPtr notificationData)
        {
            if (notificationData == IntPtr.Zero
                || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                var notification =
                    Marshal.PtrToStructure<AudioVolumeNotificationData>(
                        notificationData);
                publish(notification.Muted);
            }
            catch
            {
                publish(null);
            }
        }

        private static void ReleaseCom(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.ReleaseComObject(instance);
            }
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class DeviceNotificationClient(NativeSubscription owner)
            : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string deviceId, uint newState) => 0;

            public int OnDeviceAdded(string deviceId) => 0;

            public int OnDeviceRemoved(string deviceId) => 0;

            public int OnDefaultDeviceChanged(
                EDataFlow flow,
                ERole role,
                string? defaultDeviceId)
            {
                owner.OnDefaultDeviceChanged(flow, role);
                return 0;
            }

            public int OnPropertyValueChanged(
                string deviceId,
                PropertyKey propertyKey) => 0;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class AudioEndpointVolumeCallback(NativeSubscription owner)
            : IAudioEndpointVolumeCallback
        {
            public int OnNotify(IntPtr notificationData)
            {
                owner.OnVolumeChanged(notificationData);
                return 0;
            }
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Muted;

        public float MasterVolume;
        public uint ChannelCount;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(
            IMMNotificationClient notificationClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(
            IMMNotificationClient notificationClient);
    }

    [ComVisible(true)]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            uint newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(
            EDataFlow flow,
            ERole role,
            [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            PropertyKey propertyKey);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out nint properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComVisible(true)]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr notificationData);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(
            IAudioEndpointVolumeCallback notificationClient);

        [PreserveSig]
        int UnregisterControlChangeNotify(
            IAudioEndpointVolumeCallback notificationClient);

        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

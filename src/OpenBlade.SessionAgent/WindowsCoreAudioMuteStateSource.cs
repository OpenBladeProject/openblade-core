using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OpenBlade.SessionAgent;

internal enum CoreAudioDataFlow
{
    Render,
    Capture,
    All,
}

internal enum CoreAudioRole
{
    Console,
    Multimedia,
    Communications,
}

internal static class WindowsCoreAudioMuteStateSource
{
    public static async IAsyncEnumerable<bool?> WatchMutedAsync(
        CoreAudioDataFlow flow,
        IReadOnlyCollection<CoreAudioRole> roles,
        Func<IReadOnlyCollection<bool>, bool?> aggregate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Core Audio mute notifications require Windows.");
        }
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(aggregate);
        if (roles.Count == 0)
        {
            throw new ArgumentException(
                "At least one Core Audio endpoint role is required.",
                nameof(roles));
        }

        var updates = Channel.CreateBounded<bool?>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var subscription = new NativeSubscription(
            flow,
            roles,
            aggregate,
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
        private readonly CoreAudioDataFlow flow;
        private readonly HashSet<CoreAudioRole> roles;
        private readonly Func<IReadOnlyCollection<bool>, bool?> aggregate;
        private readonly Action<bool?> publish;
        private readonly object enumeratorObject;
        private readonly IMMDeviceEnumerator enumerator;
        private readonly DeviceNotificationClient deviceNotification;
        private readonly AudioEndpointVolumeCallback volumeNotification;
        private List<EndpointBinding> endpoints = [];
        private int disposed;

        public NativeSubscription(
            CoreAudioDataFlow flow,
            IReadOnlyCollection<CoreAudioRole> roles,
            Func<IReadOnlyCollection<bool>, bool?> aggregate,
            Action<bool?> publish)
        {
            this.flow = flow;
            this.roles = roles.ToHashSet();
            this.aggregate = aggregate;
            this.publish = publish;
            enumeratorObject = CoreAudioDeviceEnumerator.Create();
            enumerator = (IMMDeviceEnumerator)enumeratorObject;
            deviceNotification = new DeviceNotificationClient(this);
            volumeNotification = new AudioEndpointVolumeCallback(this);
            Marshal.ThrowExceptionForHR(
                enumerator.RegisterEndpointNotificationCallback(deviceNotification));
            try
            {
                BindDefaultEndpoints();
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

            List<EndpointBinding> oldEndpoints;
            lock (sync)
            {
                oldEndpoints = endpoints;
                endpoints = [];
            }

            ReleaseEndpoints(oldEndpoints);
            _ = enumerator.UnregisterEndpointNotificationCallback(deviceNotification);
            ReleaseCom(enumeratorObject);
        }

        private void BindDefaultEndpoints()
        {
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                var nextEndpoints = new List<EndpointBinding>();
                var states = new List<bool>();
                var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (CoreAudioRole role in roles)
                    {
                        TryBindEndpoint(
                            role,
                            deviceIds,
                            nextEndpoints,
                            states);
                    }

                    ReplaceEndpoints(nextEndpoints);
                    nextEndpoints = [];
                    publish(aggregate(states));
                }
                finally
                {
                    ReleaseEndpoints(nextEndpoints);
                }
            }
        }

        private void TryBindEndpoint(
            CoreAudioRole role,
            HashSet<string> deviceIds,
            List<EndpointBinding> bindings,
            List<bool> states)
        {
            object? deviceObject = null;
            object? endpointObject = null;
            IAudioEndpointVolume? endpointVolume = null;
            try
            {
                int result = enumerator.GetDefaultAudioEndpoint(
                    flow,
                    role,
                    out IMMDevice device);
                if (result == ElementNotFoundHResult)
                {
                    return;
                }

                Marshal.ThrowExceptionForHR(result);
                deviceObject = device;
                Marshal.ThrowExceptionForHR(device.GetId(out string deviceId));
                if (!deviceIds.Add(deviceId))
                {
                    return;
                }

                Guid endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(
                    ref endpointVolumeId,
                    ClassContextAll,
                    0,
                    out object endpoint));
                endpointObject = endpoint;
                endpointVolume = (IAudioEndpointVolume)endpoint;
                Marshal.ThrowExceptionForHR(
                    endpointVolume.RegisterControlChangeNotify(volumeNotification));
                Marshal.ThrowExceptionForHR(endpointVolume.GetMute(out bool muted));
                bindings.Add(new EndpointBinding(
                    deviceObject,
                    endpointObject,
                    endpointVolume));
                states.Add(muted);
                deviceObject = null;
                endpointObject = null;
                endpointVolume = null;
            }
            finally
            {
                if (endpointVolume is not null)
                {
                    _ = endpointVolume.UnregisterControlChangeNotify(
                        volumeNotification);
                }
                ReleaseCom(endpointObject);
                ReleaseCom(deviceObject);
            }
        }

        private void ReplaceEndpoints(List<EndpointBinding> nextEndpoints)
        {
            List<EndpointBinding> previousEndpoints = endpoints;
            endpoints = nextEndpoints;
            ReleaseEndpoints(previousEndpoints);
        }

        private void OnDefaultDeviceChanged(
            CoreAudioDataFlow changedFlow,
            CoreAudioRole changedRole)
        {
            if (changedFlow != flow
                || !roles.Contains(changedRole)
                || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                BindDefaultEndpoints();
            }
            catch
            {
                ClearEndpointsAndPublishFallback();
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
                bool? muted;
                lock (sync)
                {
                    var states = new List<bool>(endpoints.Count);
                    foreach (EndpointBinding endpoint in endpoints)
                    {
                        Marshal.ThrowExceptionForHR(
                            endpoint.Volume.GetMute(out bool endpointMuted));
                        states.Add(endpointMuted);
                    }
                    muted = aggregate(states);
                }
                publish(muted);
            }
            catch
            {
                publish(aggregate([]));
            }
        }

        private void ClearEndpointsAndPublishFallback()
        {
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }
                ReplaceEndpoints([]);
            }
            publish(aggregate([]));
        }

        private void ReleaseEndpoints(IEnumerable<EndpointBinding> bindings)
        {
            foreach (EndpointBinding binding in bindings)
            {
                _ = binding.Volume.UnregisterControlChangeNotify(volumeNotification);
                ReleaseCom(binding.EndpointObject);
                ReleaseCom(binding.DeviceObject);
            }
        }

        private static void ReleaseCom(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.ReleaseComObject(instance);
            }
        }

        private sealed record EndpointBinding(
            object DeviceObject,
            object EndpointObject,
            IAudioEndpointVolume Volume);

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class DeviceNotificationClient(NativeSubscription owner)
            : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string deviceId, uint newState) => 0;

            public int OnDeviceAdded(string deviceId) => 0;

            public int OnDeviceRemoved(string deviceId) => 0;

            public int OnDefaultDeviceChanged(
                CoreAudioDataFlow flow,
                CoreAudioRole role,
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            CoreAudioDataFlow dataFlow,
            uint stateMask,
            out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            CoreAudioDataFlow dataFlow,
            CoreAudioRole role,
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
            CoreAudioDataFlow flow,
            CoreAudioRole role,
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

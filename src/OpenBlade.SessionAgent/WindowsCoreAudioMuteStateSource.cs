using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OpenBlade.Contracts;

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

internal sealed class CoreAudioNotificationRegistration : IDisposable
{
    private readonly object nativeObject;
    private readonly object enumerator;
    private readonly Action<object> unregister;
    private readonly Action<object> release;
    private int registered;
    private int disposed;

    private CoreAudioNotificationRegistration(
        object nativeObject,
        object enumerator,
        Action<object> unregister,
        Action<object> release)
    {
        this.nativeObject = nativeObject;
        this.enumerator = enumerator;
        this.unregister = unregister;
        this.release = release;
    }

    internal object Enumerator => enumerator;

    internal static CoreAudioNotificationRegistration Acquire(
        Func<object> create,
        Func<object, object> validate,
        Action<object> register,
        Action<object> unregister,
        Action<object> initialize,
        Action releaseBindings,
        Action releaseManagedState,
        Action<object> release)
    {
        object? nativeObject = null;
        CoreAudioNotificationRegistration? ownership = null;
        bool initializationStarted = false;
        try
        {
            nativeObject = create();
            object enumerator = validate(nativeObject);
            ownership = new CoreAudioNotificationRegistration(
                nativeObject,
                enumerator,
                unregister,
                release);
            register(enumerator);
            Volatile.Write(ref ownership.registered, 1);
            initializationStarted = true;
            initialize(enumerator);
            return ownership;
        }
        catch
        {
            try
            {
                if (initializationStarted)
                {
                    releaseBindings();
                }
            }
            finally
            {
                try
                {
                    if (ownership is not null)
                    {
                        ownership.Dispose();
                    }
                    else if (nativeObject is not null)
                    {
                        release(nativeObject);
                    }
                }
                finally
                {
                    releaseManagedState();
                }
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref registered, 0) != 0)
            {
                unregister(enumerator);
            }
        }
        finally
        {
            release(nativeObject);
        }
    }
}

internal static class WindowsCoreAudioMuteStateSource
{
    private static readonly TimeSpan RebindFailureRetryInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

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

        private readonly CoreAudioDataFlow flow;
        private readonly HashSet<CoreAudioRole> roles;
        private readonly Func<IReadOnlyCollection<bool>, bool?> aggregate;
        private readonly Action<bool?> publish;
        private readonly CoreAudioNotificationRegistration registration;
        private readonly IMMDeviceEnumerator enumerator;
        private readonly DeviceNotificationClient deviceNotification;
        private readonly Channel<byte> rebindRequests = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly CancellationTokenSource rebindCancellation = new();
        private readonly Task rebindTask;
        private EndpointBinding[] endpoints = [];
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
            deviceNotification = new DeviceNotificationClient(this);
            registration = CoreAudioNotificationRegistration.Acquire(
                create: static () => CoreAudioDeviceEnumerator.Create(),
                validate: static instance => (IMMDeviceEnumerator)instance,
                register: instance => Marshal.ThrowExceptionForHR(
                    ((IMMDeviceEnumerator)instance)
                        .RegisterEndpointNotificationCallback(deviceNotification)),
                unregister: instance => _ = ((IMMDeviceEnumerator)instance)
                    .UnregisterEndpointNotificationCallback(deviceNotification),
                initialize: instance => BindDefaultEndpoints((IMMDeviceEnumerator)instance),
                releaseBindings: () => ReleaseEndpoints(Interlocked.Exchange(ref endpoints, [])),
                releaseManagedState: rebindCancellation.Dispose,
                release: ReleaseCom);
            enumerator = (IMMDeviceEnumerator)registration.Enumerator;
            rebindTask = RebindAsync(rebindCancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            rebindRequests.Writer.TryComplete();
            rebindCancellation.Cancel();
            CoreAudioSubscriptionShutdown.CompleteOrDefer(
                rebindTask,
                ShutdownTimeout,
                ReleaseNativeResources);
        }

        private void ReleaseNativeResources()
        {
            EndpointBinding[] oldEndpoints = Interlocked.Exchange(
                ref endpoints,
                []);
            try
            {
                ReleaseEndpoints(oldEndpoints);
            }
            finally
            {
                try
                {
                    registration.Dispose();
                }
                finally
                {
                    rebindCancellation.Dispose();
                }
            }
        }

        private async Task RebindAsync(CancellationToken cancellationToken)
        {
            while (await rebindRequests.Reader
                .WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (rebindRequests.Reader.TryRead(out _))
                {
                }

                try
                {
                    BindDefaultEndpoints();
                }
                catch
                {
                    PublishUnknown();
                    await Task.Delay(
                        RebindFailureRetryInterval,
                        cancellationToken).ConfigureAwait(false);
                    rebindRequests.Writer.TryWrite(0);
                }
            }
        }

        private void BindDefaultEndpoints() => BindDefaultEndpoints(enumerator);

        private void BindDefaultEndpoints(IMMDeviceEnumerator source)
        {
            var nextEndpoints = new List<EndpointBinding>();
            var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (CoreAudioRole role in roles)
                {
                    TryBindEndpoint(source, role, deviceIds, nextEndpoints);
                }

                EndpointBinding[] next = nextEndpoints.ToArray();
                nextEndpoints.Clear();
                EndpointBinding[] previous = Interlocked.Exchange(
                    ref endpoints,
                    next);
                PublishCurrentState();
                ReleaseEndpoints(previous);
            }
            finally
            {
                ReleaseEndpoints(nextEndpoints);
            }
        }

        private void TryBindEndpoint(
            IMMDeviceEnumerator source,
            CoreAudioRole role,
            HashSet<string> deviceIds,
            List<EndpointBinding> bindings)
        {
            object? deviceObject = null;
            object? endpointObject = null;
            IAudioEndpointVolume? endpointVolume = null;
            EndpointBinding? binding = null;
            try
            {
                int result = source.GetDefaultAudioEndpoint(
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
                binding = new EndpointBinding(
                    this,
                    deviceObject,
                    endpointObject,
                    endpointVolume);
                deviceObject = null;
                endpointObject = null;
                endpointVolume = null;
                binding.RegisterAndReadInitialState();
                bindings.Add(binding);
                binding = null;
            }
            finally
            {
                binding?.Dispose();
                ReleaseCom(endpointObject);
                ReleaseCom(deviceObject);
            }
        }

        private void RequestRebind(
            CoreAudioDataFlow changedFlow,
            CoreAudioRole changedRole)
        {
            if (changedFlow == flow
                && roles.Contains(changedRole)
                && Volatile.Read(ref disposed) == 0)
            {
                rebindRequests.Writer.TryWrite(0);
            }
        }

        private void PublishCurrentState()
        {
            EndpointBinding[] current = Volatile.Read(ref endpoints);
            if (current.Length == 0)
            {
                publish(aggregate([]));
                return;
            }

            var states = new bool[current.Length];
            for (int index = 0; index < current.Length; index++)
            {
                int muted = current[index].Muted;
                if (muted is not (0 or 1))
                {
                    PublishUnknown();
                    return;
                }
                states[index] = muted == 1;
            }
            publish(aggregate(states));
        }

        private void PublishUnknown() => publish(null);

        private static void ReleaseEndpoints(IEnumerable<EndpointBinding> bindings)
        {
            foreach (EndpointBinding binding in bindings)
            {
                binding.Dispose();
            }
        }

        private static void ReleaseCom(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.ReleaseComObject(instance);
            }
        }

        private sealed class EndpointBinding : IDisposable
        {
            private readonly NativeSubscription owner;
            private readonly object deviceObject;
            private readonly object endpointObject;
            private readonly IAudioEndpointVolume volume;
            private readonly AudioEndpointVolumeCallback volumeNotification;
            private int muted = -1;
            private int registered;
            private int disposed;

            public EndpointBinding(
                NativeSubscription owner,
                object deviceObject,
                object endpointObject,
                IAudioEndpointVolume volume)
            {
                this.owner = owner;
                this.deviceObject = deviceObject;
                this.endpointObject = endpointObject;
                this.volume = volume;
                volumeNotification = new AudioEndpointVolumeCallback(this);
            }

            public int Muted => Volatile.Read(ref muted);

            public void RegisterAndReadInitialState()
            {
                Marshal.ThrowExceptionForHR(
                    volume.RegisterControlChangeNotify(volumeNotification));
                Volatile.Write(ref registered, 1);
                Marshal.ThrowExceptionForHR(volume.GetMute(out bool initialMuted));
                Interlocked.CompareExchange(
                    ref muted,
                    initialMuted ? 1 : 0,
                    -1);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                if (Interlocked.Exchange(ref registered, 0) != 0)
                {
                    _ = volume.UnregisterControlChangeNotify(volumeNotification);
                }
                ReleaseCom(endpointObject);
                ReleaseCom(deviceObject);
            }

            private void OnVolumeChanged(IntPtr notificationData)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }
                if (notificationData == IntPtr.Zero)
                {
                    owner.PublishUnknown();
                    return;
                }

                try
                {
                    var notification =
                        Marshal.PtrToStructure<AudioVolumeNotificationData>(
                            notificationData);
                    Volatile.Write(ref muted, notification.Muted ? 1 : 0);
                    owner.PublishCurrentState();
                }
                catch
                {
                    owner.PublishUnknown();
                }
            }

            [ComVisible(true)]
            [ClassInterface(ClassInterfaceType.None)]
            private sealed class AudioEndpointVolumeCallback(EndpointBinding binding)
                : IAudioEndpointVolumeCallback
            {
                public int OnNotify(IntPtr notificationData)
                {
                    binding.OnVolumeChanged(notificationData);
                    return 0;
                }
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
                CoreAudioDataFlow flow,
                CoreAudioRole role,
                string? defaultDeviceId)
            {
                owner.RequestRebind(flow, role);
                return 0;
            }

            public int OnPropertyValueChanged(
                string deviceId,
                PropertyKey propertyKey) => 0;
        }
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

internal static class CoreAudioSubscriptionShutdown
{
    public static void CompleteOrDefer(
        Task worker,
        TimeSpan timeout,
        Action releaseResources) => CompleteOrDefer(
            worker,
            timeout,
            TimeProvider.System,
            releaseResources);

    internal static void CompleteOrDefer(
        Task worker,
        TimeSpan timeout,
        TimeProvider timeProvider,
        Action releaseResources)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var timeoutCancellation = new CancellationTokenSource();
        try
        {
            CompleteOrDefer(
                worker,
                Task.Delay(timeout, timeProvider, timeoutCancellation.Token),
                releaseResources);
        }
        finally
        {
            timeoutCancellation.Cancel();
        }
    }

    public static void CompleteOrDefer(
        Task worker,
        Task timeout,
        Action releaseResources)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentNullException.ThrowIfNull(releaseResources);

        Task completed = Task.WhenAny(worker, timeout).GetAwaiter().GetResult();
        if (completed == worker)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                releaseResources();
            }
            return;
        }

        _ = worker.ContinueWith(
            static (completedWorker, state) =>
            {
                var release = (Action)state!;
                try
                {
                    ObserveDeferredCompletion(completedWorker);
                }
                finally
                {
                    release();
                }
            },
            releaseResources,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveDeferredCompletion(Task worker)
    {
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                nameof(WindowsCoreAudioMuteStateSource),
                "ObserveDeferredCompletion",
                exception);
        }
    }
}

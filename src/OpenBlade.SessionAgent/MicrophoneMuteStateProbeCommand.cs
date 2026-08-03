using System.Runtime.InteropServices;

namespace OpenBlade.SessionAgent;

internal sealed class MicrophoneMuteStateProbeCommand(
    Func<IReadOnlyList<bool>> query,
    TextWriter output)
{
    internal const string CommandName = "--probe-microphone-mute-state";

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count == 1
        && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    internal int Run()
    {
        IReadOnlyList<bool> states = query();
        if (states.Count == 0)
        {
            output.WriteLine("DefaultCaptureEndpointCount=0");
            output.WriteLine("MuteState=Unavailable");
            output.WriteLine("Success=False");
            return 1;
        }

        bool allMuted = states.All(static muted => muted);
        bool allUnmuted = states.All(static muted => !muted);
        string state = allMuted
            ? "Muted"
            : allUnmuted
                ? "Unmuted"
                : "Mixed";
        output.WriteLine($"DefaultCaptureEndpointCount={states.Count}");
        output.WriteLine($"MuteState={state}");
        output.WriteLine("Success=True");
        return 0;
    }
}

internal static class WindowsMicrophoneMuteStateQuery
{
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private const uint ClassContextAll = 23;

    internal static IReadOnlyList<bool> Query()
    {
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = CoreAudioDeviceEnumerator.Create();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var states = new List<bool>();
            foreach (ERole role in Enum.GetValues<ERole>())
            {
                int result = enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.Capture,
                    role,
                    out IMMDevice device);
                if (result == ElementNotFoundHResult)
                {
                    continue;
                }

                Marshal.ThrowExceptionForHR(result);
                object? deviceObject = device;
                object? endpointObject = null;
                try
                {
                    Marshal.ThrowExceptionForHR(device.GetId(out string deviceId));
                    if (!deviceIds.Add(deviceId))
                    {
                        continue;
                    }

                    Guid endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(
                        ref endpointVolumeId,
                        ClassContextAll,
                        0,
                        out object endpoint));
                    endpointObject = endpoint;
                    var volume = (IAudioEndpointVolume)endpoint;
                    Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
                    states.Add(muted);
                }
                finally
                {
                    ReleaseCom(endpointObject);
                    ReleaseCom(deviceObject);
                }
            }

            return states;
        }
        finally
        {
            ReleaseCom(enumeratorObject);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
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

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
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

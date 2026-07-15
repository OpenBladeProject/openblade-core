using System.Runtime.InteropServices;

namespace OpenBlade.Service;

internal interface IGpuTemperatureSource : IDisposable
{
    int? CaptureCelsius();
}

internal interface INvmlApi : IDisposable
{
    bool IsAvailable { get; }

    bool TryInitialize();

    bool TryGetDeviceHandleByIndex(uint index, out nint device);

    bool TryGetTemperature(nint device, uint sensorType, out uint temperatureCelsius);

    void Shutdown();
}

internal sealed class NvmlGpuTemperatureSource : IGpuTemperatureSource
{
    private readonly object gate = new();
    private readonly INvmlApi api;
    private bool initialized;
    private bool disposed;

    public NvmlGpuTemperatureSource(INvmlApi api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public int? CaptureCelsius()
    {
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            try
            {
                if (!initialized)
                {
                    if (!api.IsAvailable || !api.TryInitialize())
                    {
                        return null;
                    }

                    initialized = true;
                }

                if (!api.TryGetDeviceHandleByIndex(0, out nint device) || device == 0)
                {
                    ResetInitialization();
                    return null;
                }

                if (!api.TryGetTemperature(
                        device,
                        sensorType: 0,
                        out uint temperatureCelsius)
                    || temperatureCelsius > int.MaxValue)
                {
                    ResetInitialization();
                    return null;
                }

                return (int)temperatureCelsius;
            }
            catch
            {
                ResetInitialization();
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                if (initialized)
                {
                    api.Shutdown();
                }
            }
            catch
            {
                // Telemetry teardown must not prevent service shutdown or native cleanup.
            }
            finally
            {
                initialized = false;
                try
                {
                    api.Dispose();
                }
                catch
                {
                    // The native adapter owns no state after its best-effort cleanup.
                }
            }
        }
    }

    private void ResetInitialization()
    {
        if (!initialized)
        {
            return;
        }

        try
        {
            api.Shutdown();
        }
        catch
        {
            // A reset failure must not prevent a later initialization attempt.
        }
        finally
        {
            initialized = false;
        }
    }
}

internal sealed class WindowsNvmlApi : INvmlApi
{
    private const int NvmlSuccess = 0;

    private nint libraryHandle;
    private NvmlInitDelegate? initialize;
    private NvmlDeviceGetHandleByIndexDelegate? getDeviceHandleByIndex;
    private NvmlDeviceGetTemperatureDelegate? getTemperature;
    private NvmlShutdownDelegate? shutdown;

    internal static string LibraryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "nvml.dll");

    public WindowsNvmlApi()
    {
        nint loadedLibrary = 0;
        try
        {
            if (!Path.IsPathFullyQualified(LibraryPath)
                || !NativeLibrary.TryLoad(LibraryPath, out loadedLibrary)
                || !TryGetExport(loadedLibrary, "nvmlInit_v2", out NvmlInitDelegate? loadedInitialize)
                || !TryGetExport(
                    loadedLibrary,
                    "nvmlDeviceGetHandleByIndex_v2",
                    out NvmlDeviceGetHandleByIndexDelegate? loadedGetDevice)
                || !TryGetExport(
                    loadedLibrary,
                    "nvmlDeviceGetTemperature",
                    out NvmlDeviceGetTemperatureDelegate? loadedGetTemperature)
                || !TryGetExport(loadedLibrary, "nvmlShutdown", out NvmlShutdownDelegate? loadedShutdown))
            {
                Free(loadedLibrary);
                return;
            }

            libraryHandle = loadedLibrary;
            initialize = loadedInitialize;
            getDeviceHandleByIndex = loadedGetDevice;
            getTemperature = loadedGetTemperature;
            shutdown = loadedShutdown;
        }
        catch
        {
            Free(loadedLibrary);
        }
    }

    public bool IsAvailable => libraryHandle != 0
        && initialize is not null
        && getDeviceHandleByIndex is not null
        && getTemperature is not null
        && shutdown is not null;

    public bool TryInitialize() => initialize?.Invoke() == NvmlSuccess;

    public bool TryGetDeviceHandleByIndex(uint index, out nint device)
    {
        device = 0;
        return getDeviceHandleByIndex?.Invoke(index, out device) == NvmlSuccess;
    }

    public bool TryGetTemperature(nint device, uint sensorType, out uint temperatureCelsius)
    {
        temperatureCelsius = 0;
        return getTemperature?.Invoke(device, sensorType, out temperatureCelsius) == NvmlSuccess;
    }

    public void Shutdown()
    {
        _ = shutdown?.Invoke();
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref libraryHandle, 0);
        initialize = null;
        getDeviceHandleByIndex = null;
        getTemperature = null;
        shutdown = null;
        Free(handle);
    }

    private static bool TryGetExport<TDelegate>(
        nint handle,
        string name,
        out TDelegate? export)
        where TDelegate : Delegate
    {
        export = null;
        if (handle == 0 || !NativeLibrary.TryGetExport(handle, name, out nint address))
        {
            return false;
        }

        export = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        return true;
    }

    private static void Free(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        try
        {
            NativeLibrary.Free(handle);
        }
        catch
        {
            // Missing or broken optional telemetry must remain fail closed.
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetTemperatureDelegate(
        nint device,
        uint sensorType,
        out uint temperatureCelsius);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlShutdownDelegate();
}

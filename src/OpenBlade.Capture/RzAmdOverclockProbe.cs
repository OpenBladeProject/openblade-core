using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed record CpuPowerServiceState(
    int PowerLimitWatts,
    int CurveOptimizerValue,
    string BackendVersion);

public interface ICpuPowerServiceProbe
{
    Task<CpuPowerServiceState> QueryAsync(CancellationToken cancellationToken);
}

public static class CpuPowerServiceProbeCommand
{
    public static async Task<int> RunAsync(
        ICpuPowerServiceProbe probe,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            CpuPowerServiceState state = await probe.QueryAsync(cancellationToken);
            if (state.PowerLimitWatts is < 45 or > 95
                || state.CurveOptimizerValue is < -30 or > 0
                || string.IsNullOrWhiteSpace(state.BackendVersion))
            {
                throw new InvalidOperationException(
                    "The CPU power service returned values outside the exact-device policy.");
            }

            output.WriteLine("Backend=RzAMDOverClock/RzDLLService");
            output.WriteLine($"BackendVersion={state.BackendVersion}");
            output.WriteLine($"PowerLimitWatts={state.PowerLimitWatts}");
            output.WriteLine($"CurveOptimizerValue={state.CurveOptimizerValue}");
            output.WriteLine($"CurveOptimizerEnabled={state.CurveOptimizerValue < 0}");
            output.WriteLine("ReadOnly=True");
            output.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
            or EntryPointNotFoundException
            or FileNotFoundException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or JsonException
            or PlatformNotSupportedException
            or TimeoutException)
        {
            output.WriteLine($"Failure={exception.Message}");
            output.WriteLine("ReadOnly=True");
            output.WriteLine("Success=False");
            return 2;
        }
    }
}

internal sealed class WindowsRzAmdOverclockProbe : ICpuPowerServiceProbe
{
    private const string BackendVersion = "1.1.15.0";
    private const string ClientFileName = "RzAMDOverClock_v1.1.15.0.dll";
    private const string ClientSha256 =
        "9F0EE89C1E003D2990880BABC7612FA76D0259A716B955FBC0A7D34D5DE8A418";
    private const string ServerFileName = "RzAMDOverClockDLL_v1.1.15.0.dll";
    private const string ServerSha256 =
        "7966CF2F6DEAF18A277ACAFED433063AFEC7DB9F5C4275729601AAB40B54EB9E";
    private const string ServiceFileName = "RzDLLService_v1.0.29.0.exe";
    private const string ServiceSha256 =
        "C7D47D88CDA40EC9CC84ABBBBF4B8EBAD13F5113A2DDDADEADF8A8AD6CE87436";
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    public async Task<CpuPowerServiceState> QueryAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            || !WindowsHostIdentitySource.IsExactRz090528WindowsHost())
        {
            throw new PlatformNotSupportedException(
                "The read-only CPU power probe is restricted to the exact RZ09-0528 BIOS 2.02 Windows host.");
        }

        string serviceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Razer",
            "RazerAppEngine",
            "User Data",
            "Apps",
            "Common",
            "RzDLLService");
        string clientPath = Path.Combine(serviceDirectory, ClientFileName);
        string serverPath = Path.Combine(serviceDirectory, ServerFileName);
        string servicePath = Path.Combine(serviceDirectory, ServiceFileName);
        string externalDllPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Razer",
            "RazerAMDOverClock");

        await VerifyFileAsync(clientPath, ClientSha256, cancellationToken);
        await VerifyFileAsync(serverPath, ServerSha256, cancellationToken);
        await VerifyFileAsync(servicePath, ServiceSha256, cancellationToken);
        if (!Directory.Exists(externalDllPath))
        {
            throw new FileNotFoundException(
                "The exact Synapse AMD overclock dependency directory is unavailable.",
                externalDllPath);
        }

        return await QueryNativeAsync(
            clientPath,
            serverPath,
            externalDllPath,
            cancellationToken);
    }

    private static async Task<CpuPowerServiceState> QueryNativeAsync(
        string clientPath,
        string serverPath,
        string externalDllPath,
        CancellationToken cancellationToken)
    {
        nint library = NativeLibrary.Load(clientPath);
        bool initialized = false;
        bool registered = false;
        string eventName = $"{{{Guid.NewGuid():D}}}".ToUpperInvariant();
        var messages = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        NativeMessageCallback callback = messagePointer =>
        {
            string? message = Marshal.PtrToStringUTF8(messagePointer);
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Writer.TryWrite(message);
            }
        };

        try
        {
            BoolNoArgument isServerRunning =
                GetExport<BoolNoArgument>(library, "IsAPPServerRunning");
            BoolNoArgument deviceInit =
                GetExport<BoolNoArgument>(library, "DeviceInit");
            BoolStringArgument setServerDllPath =
                GetExport<BoolStringArgument>(library, "SetServerDLLPath");
            BoolStringArgument setExternalDllPath =
                GetExport<BoolStringArgument>(library, "SetExternalDLLPath");
            BoolPointerArgument setNodeFfiEvent =
                GetExport<BoolPointerArgument>(library, "SetNodeFFIEvent");
            BoolStringArgument registerFfiEvent =
                GetExport<BoolStringArgument>(library, "RegisterFFIEvent");
            BoolNoArgument getPowerLimit =
                GetExport<BoolNoArgument>(library, "GetPowerLimitValueAsync");
            BoolNoArgument getCurveOptimizer =
                GetExport<BoolNoArgument>(library, "GetCurveOptimizerValueAsync");

            if (!isServerRunning())
            {
                throw new InvalidOperationException(
                    "RzDLLService is not already running; the read-only probe will not start it.");
            }

            initialized = deviceInit();
            if (!initialized
                || !setServerDllPath(serverPath)
                || !setExternalDllPath(externalDllPath)
                || !setNodeFfiEvent(Marshal.GetFunctionPointerForDelegate(callback))
                || !registerFfiEvent(eventName))
            {
                throw new InvalidOperationException(
                    "Unable to establish the read-only RzAMDOverClock client lifecycle.");
            }

            registered = true;
            int powerLimit = await QueryValueAsync(
                getPowerLimit,
                messages.Reader,
                "GetPowerLimitValue",
                "powerLimit",
                cancellationToken);
            int curveOptimizer = await QueryValueAsync(
                getCurveOptimizer,
                messages.Reader,
                "GetCurveOptimizerValue",
                "curveOptimizer",
                cancellationToken);

            return new CpuPowerServiceState(
                powerLimit,
                curveOptimizer,
                BackendVersion);
        }
        finally
        {
            try
            {
                if (registered)
                {
                    GetExport<BoolStringArgument>(
                        library,
                        "UnRegisterFFIEvent")(eventName);
                }
            }
            finally
            {
                if (initialized)
                {
                    GetExport<BoolNoArgument>(library, "DeviceTerminate")();
                }

                GC.KeepAlive(callback);
                NativeLibrary.Free(library);
            }
        }
    }

    private static async Task<int> QueryValueAsync(
        BoolNoArgument getter,
        ChannelReader<string> messages,
        string expectedFunction,
        string valueName,
        CancellationToken cancellationToken)
    {
        if (!getter())
        {
            throw new InvalidOperationException(
                $"{expectedFunction} did not accept the read-only request.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            while (await messages.WaitToReadAsync(timeout.Token))
            {
                while (messages.TryRead(out string? message))
                {
                    if (TryParseValue(
                        message,
                        expectedFunction,
                        valueName,
                        out int value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expectedFunction}.");
        }

        throw new InvalidDataException(
            $"{expectedFunction} ended without a response.");
    }

    internal static bool TryParseValue(
        string message,
        string expectedFunction,
        string valueName,
        out int value)
    {
        value = default;
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("dllName", out JsonElement dllName)
            && string.Equals(
                dllName.GetString(),
                "RzAMDOverClockDLL",
                StringComparison.Ordinal)
            && root.TryGetProperty("dllFunc", out JsonElement function)
            && string.Equals(
                function.GetString(),
                expectedFunction,
                StringComparison.Ordinal)
            && root.TryGetProperty("status", out JsonElement status)
            && string.Equals(status.GetString(), "SUCCESS", StringComparison.Ordinal)
            && root.TryGetProperty("error", out JsonElement error)
            && string.IsNullOrEmpty(error.GetString())
            && root.TryGetProperty(valueName, out JsonElement valueElement)
            && valueElement.TryGetInt32(out value);
    }

    private static async Task VerifyFileAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "An exact-version Synapse CPU backend component is unavailable.",
                path);
        }

        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(
            expectedSha256,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The installed {Path.GetFileName(path)} hash has not been admitted.");
        }
    }

    private static T GetExport<T>(nint library, string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool BoolNoArgument();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool BoolStringArgument(
        [MarshalAs(UnmanagedType.LPStr)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool BoolPointerArgument(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeMessageCallback(nint message);
}

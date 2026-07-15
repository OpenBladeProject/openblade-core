using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class NvmlGpuTemperatureSourceTests
{
    [Fact]
    public void CaptureCelsius_ReadsDeviceZeroTemperatureSensorZero()
    {
        var api = new FakeNvmlApi
        {
            IsAvailable = true,
            InitializeResult = true,
            DeviceResult = true,
            TemperatureResult = true,
            TemperatureCelsius = 47,
        };
        using var source = new NvmlGpuTemperatureSource(api);

        int? temperature = source.CaptureCelsius();

        Assert.Equal(47, temperature);
        Assert.Equal((uint)0, api.RequestedDeviceIndex);
        Assert.Equal((uint)0, api.RequestedSensorType);
    }

    [Fact]
    public void CaptureCelsius_MissingLibraryOrExportIsUnavailable()
    {
        var api = new FakeNvmlApi { IsAvailable = false };
        using var source = new NvmlGpuTemperatureSource(api);

        Assert.Null(source.CaptureCelsius());
        Assert.Equal(0, api.InitializeCount);
        Assert.Equal(0, api.DeviceCount);
        Assert.Equal(0, api.TemperatureCount);
    }

    [Fact]
    public void CaptureCelsius_TransientInitializationFailureRetriesOnLaterCapture()
    {
        var api = FakeNvmlApi.Successful();
        api.InitializeFailuresRemaining = 1;
        using var source = new NvmlGpuTemperatureSource(api);

        int? first = source.CaptureCelsius();
        int? second = source.CaptureCelsius();

        Assert.Null(first);
        Assert.Equal(47, second);
        Assert.Equal(2, api.InitializeCount);
        Assert.Equal(1, api.DeviceCount);
        Assert.Equal(1, api.TemperatureCount);
    }

    [Theory]
    [InlineData(FailureStage.Device)]
    [InlineData(FailureStage.Temperature)]
    public void CaptureCelsius_PostInitializationFailureReinitializesOnLaterCapture(
        FailureStage failureStage)
    {
        var api = FakeNvmlApi.Successful();
        api.TransientFailureStage = failureStage;
        using var source = new NvmlGpuTemperatureSource(api);

        int? first = source.CaptureCelsius();
        int? second = source.CaptureCelsius();

        Assert.Null(first);
        Assert.Equal(47, second);
        Assert.Equal(2, api.InitializeCount);
        Assert.Equal(1, api.ShutdownCount);
    }

    [Theory]
    [InlineData(FailureStage.Device)]
    [InlineData(FailureStage.Temperature)]
    public void CaptureCelsius_PostInitializationExceptionReinitializesOnLaterCapture(
        FailureStage failureStage)
    {
        var api = FakeNvmlApi.Successful();
        api.TransientExceptionStage = failureStage;
        using var source = new NvmlGpuTemperatureSource(api);

        int? first = source.CaptureCelsius();
        int? second = source.CaptureCelsius();

        Assert.Null(first);
        Assert.Equal(47, second);
        Assert.Equal(2, api.InitializeCount);
        Assert.Equal(1, api.ShutdownCount);
    }

    [Theory]
    [InlineData(FailureStage.Initialize)]
    [InlineData(FailureStage.Device)]
    [InlineData(FailureStage.Temperature)]
    public void CaptureCelsius_NvmlFailureReturnsUnavailable(FailureStage failureStage)
    {
        var api = FakeNvmlApi.Successful();
        api.FailedStage = failureStage;
        using var source = new NvmlGpuTemperatureSource(api);

        Assert.Null(source.CaptureCelsius());
    }

    [Theory]
    [InlineData(FailureStage.Initialize)]
    [InlineData(FailureStage.Device)]
    [InlineData(FailureStage.Temperature)]
    public void CaptureCelsius_NvmlExceptionReturnsUnavailable(FailureStage failureStage)
    {
        var api = FakeNvmlApi.Successful();
        api.ThrownStage = failureStage;
        using var source = new NvmlGpuTemperatureSource(api);

        Exception? exception = Record.Exception(() => source.CaptureCelsius());

        Assert.Null(exception);
        Assert.Null(source.CaptureCelsius());
    }

    [Fact]
    public void Dispose_ShutsDownInitializedNvmlAndFreesLibraryExactlyOnce()
    {
        var api = FakeNvmlApi.Successful();
        var source = new NvmlGpuTemperatureSource(api);
        Assert.Equal(47, source.CaptureCelsius());

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, api.ShutdownCount);
        Assert.Equal(1, api.DisposeCount);
        Assert.Null(source.CaptureCelsius());
    }

    [Fact]
    public void Dispose_FreesLibraryWhenShutdownThrows()
    {
        var api = FakeNvmlApi.Successful();
        api.ThrownStage = FailureStage.Shutdown;
        var source = new NvmlGpuTemperatureSource(api);
        Assert.Equal(47, source.CaptureCelsius());

        Exception? exception = Record.Exception(source.Dispose);

        Assert.Null(exception);
        Assert.Equal(1, api.ShutdownCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact]
    public void WindowsAdapterPath_IsOnlySystem32NvmlDll()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvml.dll");

        Assert.Equal(expected, WindowsNvmlApi.LibraryPath);
        Assert.True(Path.IsPathFullyQualified(WindowsNvmlApi.LibraryPath));
    }

    public enum FailureStage
    {
        None,
        Initialize,
        Device,
        Temperature,
        Shutdown,
    }

    private sealed class FakeNvmlApi : INvmlApi
    {
        public bool IsAvailable { get; init; }
        public bool InitializeResult { get; init; }
        public bool DeviceResult { get; init; }
        public bool TemperatureResult { get; init; }
        public uint TemperatureCelsius { get; init; }
        public FailureStage FailedStage { get; set; }
        public FailureStage ThrownStage { get; set; }
        public FailureStage TransientFailureStage { get; set; }
        public FailureStage TransientExceptionStage { get; set; }
        public int InitializeFailuresRemaining { get; set; }
        public int InitializeCount { get; private set; }
        public int DeviceCount { get; private set; }
        public int TemperatureCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int DisposeCount { get; private set; }
        public uint? RequestedDeviceIndex { get; private set; }
        public uint? RequestedSensorType { get; private set; }

        public static FakeNvmlApi Successful() => new()
        {
            IsAvailable = true,
            InitializeResult = true,
            DeviceResult = true,
            TemperatureResult = true,
            TemperatureCelsius = 47,
        };

        public bool TryInitialize()
        {
            InitializeCount++;
            ThrowIfRequested(FailureStage.Initialize);
            if (InitializeFailuresRemaining > 0)
            {
                InitializeFailuresRemaining--;
                return false;
            }

            return FailedStage != FailureStage.Initialize && InitializeResult;
        }

        public bool TryGetDeviceHandleByIndex(uint index, out nint device)
        {
            DeviceCount++;
            RequestedDeviceIndex = index;
            ThrowIfRequested(FailureStage.Device);
            ThrowTransientIfRequested(FailureStage.Device);
            if (ConsumeTransientFailure(FailureStage.Device))
            {
                device = 0;
                return false;
            }

            device = DeviceResult ? (nint)42 : 0;
            return FailedStage != FailureStage.Device && DeviceResult;
        }

        public bool TryGetTemperature(nint device, uint sensorType, out uint temperatureCelsius)
        {
            TemperatureCount++;
            RequestedSensorType = sensorType;
            ThrowIfRequested(FailureStage.Temperature);
            ThrowTransientIfRequested(FailureStage.Temperature);
            if (ConsumeTransientFailure(FailureStage.Temperature))
            {
                temperatureCelsius = 0;
                return false;
            }

            temperatureCelsius = TemperatureCelsius;
            return FailedStage != FailureStage.Temperature && TemperatureResult;
        }

        public void Shutdown()
        {
            ShutdownCount++;
            ThrowIfRequested(FailureStage.Shutdown);
        }

        public void Dispose() => DisposeCount++;

        private void ThrowIfRequested(FailureStage stage)
        {
            if (ThrownStage == stage)
            {
                throw new InvalidOperationException("Injected NVML failure.");
            }
        }

        private void ThrowTransientIfRequested(FailureStage stage)
        {
            if (TransientExceptionStage == stage)
            {
                TransientExceptionStage = FailureStage.None;
                throw new InvalidOperationException("Injected transient NVML failure.");
            }
        }

        private bool ConsumeTransientFailure(FailureStage stage)
        {
            if (TransientFailureStage != stage)
            {
                return false;
            }

            TransientFailureStage = FailureStage.None;
            return true;
        }
    }
}

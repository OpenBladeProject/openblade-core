using System.Runtime.InteropServices;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioHelperCommandTests
{
    [Fact]
    public void VendorJson_ParsesSourceBackedPrimitiveShapes()
    {
        Assert.True(ThxVendorJson.ParseBoolean("true"));
        Assert.False(ThxVendorJson.ParseBoolean("false"));
        Assert.Equal(
            [new ThxAudioPreset("music", "music"), new ThxAudioPreset("game", "game")],
            ThxVendorJson.ParsePresetList("[\"music\",\"game\"]"));
        Assert.Equal("music", ThxVendorJson.ParseSelectedPreset("\"music\""));
        Assert.Null(ThxVendorJson.ParseSelectedPreset("null"));
    }

    [Fact]
    public void VendorJson_RejectsMalformedOrUnboundedShapes()
    {
        Assert.Throws<InvalidDataException>(() => ThxVendorJson.ParseBoolean("1"));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParsePresetList("[\"music\",\"music\"]"));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParseSelectedPreset("{\"presetId\":\"music\"}"));
    }

    [Fact]
    public async Task Read_DoesNotSetPropertiesAndAlwaysRemovesDevice()
    {
        var native = new FakeNative();
        string input = """
            {"operation":"read","version":1,"endpoint":0}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(native.PropertyWrites);
        Assert.Equal(1, native.AddCalls);
        Assert.Equal(1, native.RemoveCalls);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutation_ReturnsIndeterminateWhenReadbackDoesNotMatch()
    {
        var native = new FakeNative { Spatial = false };
        string input = """
            {"operation":"setProperty","version":1,"endpoint":0,"propertyId":"spatial_enabled","value":true}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Single(native.PropertyWrites);
        Assert.Equal("spatial_enabled", native.PropertyWrites[0].PropertyId);
        Assert.Equal("true", native.PropertyWrites[0].JsonValue);
        Assert.Null(native.PropertyWrites[0].PropertyType);
        Assert.Contains("\"status\":3", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, native.RemoveCalls);
    }

    [Fact]
    public async Task CommandCore_DeniedAdmissionDoesNotConstructNativeAdapter()
    {
        bool constructed = false;
        var admission = new FixedAdmission(new ThxAudioAdmission(
            ThxAudioAvailability.UnsupportedModel,
            "Unsupported test host."));
        string input = """
            {"operation":"read","version":1,"endpoint":0}
            """;
        var output = new StringWriter();
        var command = new ThxAudioHelperCommand(
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            admission,
            new StringReader(input),
            output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(constructed);
        Assert.Contains("\"status\":1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandCore_RejectsOversizedInputBeforeAdmissionOrNativeConstruction()
    {
        int admissionChecks = 0;
        bool constructed = false;
        string input = new('x', ThxAudioHelperProtocol.MaximumLineCharacters + 1);
        var output = new StringWriter();
        var command = new ThxAudioHelperCommand(
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            new CountingAdmission(() => admissionChecks++),
            new StringReader(input),
            output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, admissionChecks);
        Assert.False(constructed);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelperHost_UntrustedParentCannotConstructNativeAdapter()
    {
        bool constructed = false;
        var channel = new FakeClientChannel(
            "{\"operation\":\"read\",\"version\":1,\"endpoint\":0}");
        var host = new ThxAudioHelperHost(
            new FakeConnector(channel),
            new FixedParentVerifier(trusted: false),
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)));
        int parentProcessId = 42;
        var invocation = new ThxAudioHelperInvocation(
            ThxAudioHelperProtocol.CreatePipeName(parentProcessId, Guid.NewGuid()),
            parentProcessId);

        int exitCode = await host.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.False(constructed);
        Assert.Equal(string.Empty, channel.OutputText);
    }

    [Fact]
    public void HelperInvocation_RequiresFixedPidBoundNonceShape()
    {
        int parentProcessId = 42;
        string pipeName = ThxAudioHelperProtocol.CreatePipeName(
            parentProcessId,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));

        Assert.True(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, pipeName, "42"],
            out ThxAudioHelperInvocation? invocation));
        Assert.Equal(parentProcessId, invocation!.ParentProcessId);
        Assert.False(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, pipeName, "43"],
            out _));
        Assert.False(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, $"{pipeName}x", "42"],
            out _));
    }

    [Fact]
    public async Task ProcessClient_AssignmentFailureSendsNoRequest()
    {
        var child = new FakeChild
        {
            AssignResult = false,
            TerminateResult = true,
            HasExitedValue = true,
        };
        var factory = new FakeChildFactory(child);
        await using var client = new ThxAudioHelperProcessClient(childFactory: factory);

        ThxAudioWorkerResult result = await client.ExecuteAsync(
            new ThxHelperReadRequest(ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Failed, result.Status);
        Assert.Equal(0, child.ExchangeCalls);
        Assert.Equal(1, child.TerminateCalls);
    }

    [Fact]
    public async Task ProcessClient_UnconfirmedExitBlocksSubsequentOperations()
    {
        var child = new FakeChild
        {
            AssignResult = true,
            ConfirmExitResult = false,
            TerminateResult = false,
            HasExitedValue = false,
            Response = SuccessfulHelperResponse(spatial: true),
        };
        var factory = new FakeChildFactory(child);
        await using var client = new ThxAudioHelperProcessClient(childFactory: factory);

        ThxAudioWorkerResult uncertain = await client.ExecuteAsync(
            new ThxHelperSetPropertyRequest(
                ThxAudioHelperProtocol.Version,
                ThxAudioEndpoint.Speakers,
                "spatial_enabled",
                true),
            CancellationToken.None);
        ThxAudioWorkerResult blocked = await client.ExecuteAsync(
            new ThxHelperReadRequest(ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Indeterminate, uncertain.Status);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, blocked.Status);
        Assert.Equal(1, factory.StartCalls);
        Assert.Equal(1, child.ExchangeCalls);
    }

    [Fact]
    public async Task SelectPreset_RejectsIdMissingFromFreshVendorListBeforeMutation()
    {
        var native = new FakeNative();
        string input = """
            {"operation":"selectPreset","version":1,"endpoint":0,"presetId":"movie"}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, native.PresetListCalls);
        Assert.Equal(0, native.SelectPresetCalls);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, native.RemoveCalls);
    }

    [Theory]
    [InlineData("USB\\VID_1532&PID_02E0", true)]
    [InlineData("USB\\VID_1532&PID_02E0\\ABC", true)]
    [InlineData("USB\\VID_1532&PID_02E0&MI_00\\ABC", true)]
    [InlineData("USB\\VID_1532&PID_02E00\\ABC", false)]
    [InlineData("USB\\VID_1532&PID_02E0X\\ABC", false)]
    public void UsbAdmission_RequiresExactPidBoundary(string pnpDeviceId, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsThxAudioAdmissionSource.IsExactUsbPnpDeviceId(pnpDeviceId));
    }

    [Fact]
    public void NativeDelegates_AreCdeclAndBooleanReturnsAreOneByte()
    {
        Type[] delegates = typeof(ThxAudioNativeAdapter).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic);
        Type[] nativeDelegates = delegates.Where(type =>
            type.BaseType == typeof(MulticastDelegate)).ToArray();

        Assert.NotEmpty(nativeDelegates);
        foreach (Type type in nativeDelegates)
        {
            var convention = type.GetCustomAttributes(
                typeof(UnmanagedFunctionPointerAttribute),
                inherit: false).Cast<UnmanagedFunctionPointerAttribute>().Single();
            Assert.Equal(CallingConvention.Cdecl, convention.CallingConvention);
            if (type.GetMethod("Invoke")!.ReturnType == typeof(bool))
            {
                var marshal = type.GetMethod("Invoke")!.ReturnParameter
                    .GetCustomAttributes(typeof(MarshalAsAttribute), inherit: false)
                    .Cast<MarshalAsAttribute>()
                    .Single();
                Assert.Equal(UnmanagedType.I1, marshal.Value);
            }
        }
    }

    private sealed class FakeNative : IThxAudioNativeAdapter
    {
        public bool Spatial { get; set; }
        public bool Equalizer { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int PresetListCalls { get; private set; }
        public int SelectPresetCalls { get; private set; }
        public List<(string PropertyId, string JsonValue, string? PropertyType)> PropertyWrites
        {
            get;
        } = [];

        public bool AddDevice(string deviceJson)
        {
            AddCalls++;
            return true;
        }

        public bool CheckStatus(string deviceJson) => true;

        public bool RemoveDevice(string deviceJson)
        {
            RemoveCalls++;
            return true;
        }

        public string GetProperty(string deviceJson, string propertyId) => propertyId switch
        {
            "spatial_enabled" => Spatial ? "true" : "false",
            "eq_enabled" => Equalizer ? "true" : "false",
            _ => throw new InvalidOperationException(),
        };

        public bool SetProperty(
            string deviceJson,
            string propertyId,
            string jsonValue,
            string? propertyType)
        {
            PropertyWrites.Add((propertyId, jsonValue, propertyType));
            // Deliberately do not update readback; the helper must not guess success.
            return true;
        }

        public string GetPresetList(string deviceJson, string presetGroupId)
        {
            PresetListCalls++;
            return "[\"music\",\"game\"]";
        }

        public string GetSelectedPreset(string deviceJson, string presetGroupId) => "\"music\"";

        public bool SelectPreset(string deviceJson, string presetGroupId, string presetId)
        {
            SelectPresetCalls++;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static ThxAudioHelperCommand Command(
        FakeNative native,
        string input,
        StringWriter output) => new(
            () => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)),
            new StringReader(input),
            output);

    private sealed class FixedAdmission(ThxAudioAdmission value) : IThxAudioAdmissionSource
    {
        public ThxAudioAdmission Check() => value;
    }

    private sealed class CountingAdmission(Action observed) : IThxAudioAdmissionSource
    {
        public ThxAudioAdmission Check()
        {
            observed();
            return new ThxAudioAdmission(ThxAudioAvailability.Available, null);
        }
    }

    private sealed class FakeClientChannel(string request) : IThxAudioHelperClientChannel
    {
        private readonly StringWriter output = new();

        public TextReader Input { get; } = new StringReader(request);

        public TextWriter Output => output;

        public string OutputText => output.ToString();

        public ValueTask DisposeAsync()
        {
            Input.Dispose();
            output.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnector(IThxAudioHelperClientChannel channel)
        : IThxAudioHelperChannelConnector
    {
        public Task<IThxAudioHelperClientChannel> ConnectAsync(
            ThxAudioHelperInvocation invocation,
            CancellationToken cancellationToken) => Task.FromResult(channel);
    }

    private sealed class FixedParentVerifier(bool trusted) : IThxAudioHelperParentVerifier
    {
        public bool IsTrustedParent(
            IThxAudioHelperClientChannel channel,
            int expectedParentProcessId) => trusted;
    }

    private sealed class FakeChildFactory(IThxAudioHelperChild child)
        : IThxAudioHelperChildFactory
    {
        public int StartCalls { get; private set; }

        public IThxAudioHelperChild Start()
        {
            StartCalls++;
            return child;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeChild : IThxAudioHelperChild
    {
        public bool AssignResult { get; init; }
        public bool ConfirmExitResult { get; init; } = true;
        public bool TerminateResult { get; init; } = true;
        public bool HasExitedValue { get; init; } = true;
        public int ExchangeCalls { get; private set; }
        public int TerminateCalls { get; private set; }
        public ThxAudioHelperResponse Response { get; init; } = SuccessfulHelperResponse();

        public bool HasExited => HasExitedValue;

        public int? ExitCode => HasExitedValue ? 0 : null;

        public bool AssignToLifetimeOwner() => AssignResult;

        public Task<ThxAudioHelperResponse> ExchangeAsync(
            ThxAudioHelperRequest request,
            CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            return Task.FromResult(Response);
        }

        public Task<bool> ConfirmExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ConfirmExitResult);

        public Task<bool> TerminateAndConfirmExitAsync()
        {
            TerminateCalls++;
            return Task.FromResult(TerminateResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ThxAudioHelperResponse SuccessfulHelperResponse(bool spatial = false) => new(
        ThxAudioHelperProtocol.Version,
        ThxAudioOperationStatus.Succeeded,
        ThxAudioAvailability.Available,
        null,
        new ThxAudioState(
            ThxAudioEndpoint.Speakers,
            spatial,
            EqualizerEnabled: false,
            SelectedPresetId: "music",
            Presets: [new ThxAudioPreset("music", "music")]));
}

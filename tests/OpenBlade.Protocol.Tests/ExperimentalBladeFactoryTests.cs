using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeFactoryTests
{
    [Fact]
    public async Task Blade18LightingAdapter_SharesConsentAndRetainedLowPowerAdmission()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io();
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);
        IProductionLightingPortFactory lighting = factory.CreateLightingFactory();
        Assert.Empty(lighting.Capabilities.LightingEffects);
        Assert.Contains(LightingEffect.Spectrum, factory.PotentialCapabilities.LightingEffects);
        factory.SetEnabled(true);
        Assert.True(lighting.Capabilities.SupportsKeyboardBrightness);
        bool active = true;
        HidSessionResult<IProductionLightingPort> opened = await lighting.OpenAsync(() => active, CancellationToken.None);
        Assert.Null(opened.Failure);
        await using IProductionLightingPort port = Assert.IsType<ExperimentalBladePort>(opened.Value);
        Assert.Equal(new byte[] { 0x82, 0x82 }, io.Commands);
        active = false;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(77), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(2, io.Commands.Count);
        factory.SetEnabled(false);
        Assert.Empty(port.Capabilities.LightingEffects);
        Assert.False(port.Capabilities.SupportsKeyboardBrightness);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    public async Task LightingAdapter_OtherExperimentalModelsDoNotOpenHid(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io();
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);
        factory.SetEnabled(true);
        HidSessionResult<IProductionLightingPort> result = await factory.CreateLightingFactory().OpenAsync(CancellationToken.None);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
        Assert.Empty(io.Commands);
        Assert.Equal(0, native.BoundCount);
    }

    [Fact]
    public async Task CancellationAfterSelectingFirstInterface_ReturnsCanceledAndDisposesBothHandles()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native(2) { BeforeSecondInterfaceRecheck = cancellation.Cancel };
        var io = new Io();
        var factory = Create(native, io);

        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(cancellation.Token);

        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(2, native.BoundCount);
        Assert.All(native.DisposeCounts, count => Assert.Equal(1, count));
        Assert.Equal(2, io.Commands.Count);
        Assert.All(io.Commands, command => Assert.Equal((byte)0x82, command));
    }

    [Fact]
    public async Task CancellationDuringFunctionalProbe_ReturnsCanceledAndDisposesHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native(1);
        var io = new Io { AfterGet = cancellation.Cancel };
        var factory = Create(native, io);

        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(cancellation.Token);

        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(1, native.BoundCount);
        Assert.Equal(1, native.DisposeCounts[0]);
        Assert.Single(io.Commands);
        Assert.Equal((byte)0x82, io.Commands[0]);
    }

    [Fact]
    public async Task NonRespondingCandidate_DoesNotPolluteAcceptedInterfaceHistory()
    {
        var native = new Native(2);
        var io = new Io { RejectSecondInterface = true };
        var factory = Create(native, io);
        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(CancellationToken.None);
        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        await using IProductionBatteryPort port = result.Value!;
        Assert.Empty(factory.GetDiagnostics().Observations);
        await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        ExperimentalControlObservation observation = Assert.Single(factory.GetDiagnostics().Observations);
        Assert.Equal("Confirmed", observation.Outcome);
        Assert.Equal("Performance", observation.Operation);
    }

    [Fact]
    public async Task NoAdmittedInterface_RetainsDecodedUnsupportedModeReason()
    {
        var factory = Create(new Native(1), new Io { PerformanceMode = 99 });
        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(CancellationToken.None);
        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
        Assert.Contains(factory.GetDiagnostics().Observations,
            item => item.Outcome == "UnsupportedState" && item.ConfirmedValue == "ReportedMode=99");
    }

    private static ExperimentalBladeFactory Create(Native native, Io io) =>
        new(ExperimentalBladeModels.All[0], new Identity(), native, new Guard(), io,
            () => PowerSource.ExternalUnknown);

    private sealed class Identity(string product = "Blade 16 - RZ09-0483") : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity() => new(
            new HostIdentityEvidence("Razer", product, null, "test-firmware"), null);
    }

    private sealed class Guard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }

    private sealed class Native : IHidCandidateNative
    {
        private readonly OpenedCandidateHandle[] handles;
        private readonly int[] reads;
        internal ushort ProductId { get; init; } = 0x029F;
        internal int[] DisposeCounts { get; }
        internal int BoundCount { get; private set; }
        internal Action? BeforeSecondInterfaceRecheck { get; init; }

        internal Native(int count)
        {
            DisposeCounts = new int[count];
            reads = new int[count];
            handles = Enumerable.Range(0, count).Select(index =>
            {
                var file = new SafeFileHandle(new IntPtr(500 + index), ownsHandle: false);
                return new OpenedCandidateHandle(file, () =>
                {
                    DisposeCounts[index]++;
                    file.Dispose();
                });
            }).ToArray();
        }

        public IReadOnlyList<string> EnumerateDevicePaths() =>
            Enumerable.Range(0, handles.Length).Select(index => index.ToString()).ToArray();

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(handles[int.Parse(path)], null);

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            int index = Array.IndexOf(handles, handle);
            if (++reads[index] == 2 && index == 1)
            {
                BeforeSecondInterfaceRecheck?.Invoke();
            }
            return new(0x1532, ProductId, 1, 2, 91);
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            int index = Array.IndexOf(handles, handle);
            BoundCount++;
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            return new(file, bound, () => { }, () =>
            {
                DisposeCounts[index]++;
                file.Dispose();
            });
        }
    }

    private sealed class Io : IHidFeatureIo
    {
        private byte[] lastRequest = [];
        internal List<byte> Commands { get; } = [];
        internal Action? AfterGet { get; init; }
        internal bool RejectSecondInterface { get; init; }
        internal byte PerformanceMode { get; init; }

        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode,
            byte[] buffer, CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            if (controlCode == 0x000B0191)
            {
                lastRequest = buffer.ToArray();
                Commands.Add(buffer[8]);
            }
            else
            {
                if (RejectSecondInterface && handle.FileHandle.DangerousGetHandle() == new IntPtr(501))
                {
                    return Task.FromResult(new HidFeatureIoResult(new(false, 0, 31), false,
                        HidFeatureTermination.Completed));
                }
                lastRequest.CopyTo(buffer, 0);
                buffer[1] = 0x02;
                buffer[11] = PerformanceMode;
                buffer[89] ^= PerformanceMode;
                AfterGet?.Invoke();
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }
    }
}

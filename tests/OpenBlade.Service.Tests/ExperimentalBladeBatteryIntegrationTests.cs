using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class ExperimentalBladeBatteryIntegrationTests
{
    [Fact]
    public async Task Provider_AdmitsRangeChangesFromOffAndConfirmsExactLimit()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var io = new Io { Raw = 0x50 };
        var factory = new ExperimentalBladeFactory(model, new Identity(), new Native(), new Guard(), io,
            () => PowerSource.ExternalUnknown);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory());
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState baseline = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(baseline.Battery!.ProtectionEnabled);
        Assert.False(baseline.Battery.TemporaryFullCharge);
        var coordinator = new ControlCoordinator(device);
        var desired = new DesiredBatteryState(BatteryPolicy.Create(99), true, false);
        ScopedApplyOutcome changed = await coordinator.ApplyBatteryOutcomeAsync(desired, CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(99, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.False(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(0xE3, io.Raw);
        Assert.Single(io.Setters);
        Assert.Equal((byte)0xE3, io.Setters[0]);
        ScopedApplyOutcome disabled = await coordinator.ApplyBatteryOutcomeAsync(
            new DesiredBatteryState(BatteryPolicy.Create(80), false, false), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, disabled.Result.Status);
        Assert.False(disabled.Readback!.Battery!.ProtectionEnabled);
        Assert.Null(disabled.Readback.Battery.ChargeLimitPercent);
        Assert.Equal(new byte[] { 0xE3, 0x50 }, io.Setters);

    }

    [Fact]
    public async Task Recovery_FreshReadbackIsAcceptedBeforePauseClearsWithoutSetterReplay()
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        IBladeDevice device = await PauseAsync(provider, io);
        int setterCount = io.Setters.Count;
        int commandCount = io.Commands.Count;
        bool accepted = false;
        ConfirmedDeviceState observed = await provider.ResumeExperimentalWritesAsync(state =>
        {
            Assert.True(provider.IsExperimentalWritesPaused);
            Assert.True(state.Battery!.ProtectionEnabled);
            Assert.Equal(80, state.Battery.ChargeLimitPercent);
            Assert.False(state.Battery.TemporaryFullCharge);
            Assert.Equal(PerformancePreset.Balanced, state.Performance);
            Assert.Equal(FanMode.Automatic, state.Fans);
            Assert.Equal(LightingEffect.Spectrum, state.Lighting!.Effect);
            Assert.Equal(LogoLightingMode.Off, state.LogoLighting);
            accepted = true;
            return true;
        }, () => true, CancellationToken.None);
        Assert.True(accepted);
        Assert.False(provider.IsExperimentalWritesPaused);
        Assert.False(provider.GetExperimentalSupportDiagnostics().WritesPaused);
        Assert.Equal(setterCount, io.Setters.Count);
        Assert.All(io.Commands.Skip(commandCount), command => Assert.True(command.Id >= 0x80));
        Assert.Contains(provider.GetExperimentalSupportDiagnostics().Observations,
            item => item.Operation == "WriteRecovery" && item.Outcome == "Resumed" && !item.Submitted);
        Assert.Equal(80, observed.Battery!.ChargeLimitPercent);
        await device.SetChargeLimitAsync(BatteryPolicy.Create(90), CancellationToken.None);
        Assert.Equal(setterCount + 1, io.Setters.Count);
        Assert.Equal(0xDA, io.Raw);
    }

    [Fact]
    public async Task Recovery_RetiresRetainedLightingBeforeFreshAdmissionAndSerializesReads()
    {
        var io = new Io { Raw = 0x50 };
        var native = new Native();
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.ReadLightingStateAsync(CancellationToken.None);
        Assert.Equal(1, native.ActiveSessions);
        await PauseAsync(provider, io);
        Assert.Equal(1, native.ActiveSessions);
        int closedBeforeRecovery = native.ClosedSessions;
        Task<ConfirmedDeviceState>? concurrentRead = null;
        await provider.ResumeExperimentalWritesAsync(_ =>
        {
            Assert.Equal(closedBeforeRecovery + 2, native.ClosedSessions);
            Assert.Equal(0, native.ActiveSessions);
            concurrentRead = device.ReadLightingStateAsync(CancellationToken.None);
            Assert.False(concurrentRead.IsCompleted);
            return true;
        }, () => true, CancellationToken.None);
        Assert.NotNull(concurrentRead);
        ConfirmedDeviceState readback = await concurrentRead;
        Assert.Equal(LightingEffect.Spectrum, readback.Lighting!.Effect);
        Assert.False(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Fact]
    public async Task Recovery_RejectedReconciliationLeavesPauseAndPreventsLaterWrites()
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        IBladeDevice device = await PauseAsync(provider, io);
        int setterCount = io.Setters.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => provider.ResumeExperimentalWritesAsync(
            _ => false, () => true, CancellationToken.None));
        Assert.True(provider.IsExperimentalWritesPaused);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetChargeLimitAsync(BatteryPolicy.Create(90), CancellationToken.None));
        Assert.Equal(setterCount, io.Setters.Count);
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("lighting")]
    [InlineData("performance")]
    public async Task Recovery_UnrepresentableReadbackLeavesPause(string malformed)
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        await PauseAsync(provider, io);
        if (malformed == "battery")
        {
            io.Raw = 0x81;
        }
        else if (malformed == "lighting")
        {
            io.Effect = 99;
        }
        else
        {
            io.Mode = 99;
        }
        bool accepted = false;
        await Assert.ThrowsAsync<IOException>(() => provider.ResumeExperimentalWritesAsync(
            _ => { accepted = true; return true; }, () => true, CancellationToken.None));
        Assert.False(accepted);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("authorization")]
    [InlineData("identity")]
    [InlineData("controller")]
    public async Task Recovery_FinalAdmissionFailureLeavesPause(string failure)
    {
        var io = new Io { Raw = 0x50 };
        var identity = new Identity();
        var guard = new Guard();
        using var cancellation = new CancellationTokenSource();
        bool authorized = true;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, identity, guard);
        await PauseAsync(provider, io);
        bool accepted = false;
        io.AfterLogoRead = () =>
        {
            switch (failure)
            {
                case "cancel": cancellation.Cancel(); break;
                case "authorization": authorized = false; break;
                case "identity": identity.Present = false; break;
                case "controller": guard.Fail = true; break;
            }
        };
        Exception? exception = await Record.ExceptionAsync(() => provider.ResumeExperimentalWritesAsync(
            _ => { accepted = true; return true; }, () => authorized, cancellation.Token));
        Assert.False(accepted);
        Assert.NotNull(exception);
        if (failure == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        else
        {
            Assert.IsType<CapabilityUnavailableException>(exception);
        }
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Fact]
    public async Task Recovery_UnpausedProviderDoesNotSubmitCommands()
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => provider.ResumeExperimentalWritesAsync(
            _ => true, () => true, CancellationToken.None));
        Assert.Empty(io.Commands);
    }

    private static WindowsVerifiedBladeDeviceProvider CreateProvider(Io io, Identity? identity = null, Guard? guard = null, Native? native = null)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var factory = new ExperimentalBladeFactory(model, identity ?? new Identity(), native ?? new Native(), guard ?? new Guard(), io,
            () => PowerSource.ExternalUnknown);
        return new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory(), factory.CreateLightingFactory());
    }

    private static async Task<IBladeDevice> PauseAsync(WindowsVerifiedBladeDeviceProvider provider, Io io)
    {
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        io.FailNextSetterResponse = true;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetChargeLimitAsync(BatteryPolicy.Create(80), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.True(provider.GetExperimentalSupportDiagnostics().WritesPaused);
        Assert.Single(io.Setters);
        return device;
    }

    private sealed class Identity : IHostIdentitySource
    {
        internal bool Present { get; set; } = true;
        public HostIdentityReadResult ReadExactIdentity() => Present
            ? new(new HostIdentityEvidence("Razer", "Blade 18 - RZ09-0484", null, "test"), null)
            : new(null, HidSessionFailureKind.IdentityBlocked);
    }
    private sealed class Guard : IHardwareAccessGuard
    {
        internal bool Fail { get; set; }
        public HardwareAccessDecision Check() => new([], Fail ? "Unavailable" : null);
    }
    private sealed class Native : IHidCandidateNative
    {
        internal int ActiveSessions { get; private set; }
        internal int ClosedSessions { get; private set; }
        public IReadOnlyList<string> EnumerateDevicePaths() => ["test"];
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(501), ownsHandle: false)), null);
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => new(0x1532, 0x02A0, 1, 2, 91);
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            ActiveSessions++;
            return new(file, bound, () => { }, () =>
            {
                ActiveSessions--;
                ClosedSessions++;
                file.Dispose();
            });
        }
    }
    private sealed class Io : IHidFeatureIo
    {
        private byte[] request = [];
        internal byte Raw { get; set; }
        internal byte Mode { get; set; }
        internal byte Effect { get; set; } = 3;
        internal bool FailNextSetterResponse { get; set; }
        internal Action? AfterLogoRead { get; set; }
        internal List<(byte Class, byte Id)> Commands { get; } = [];
        internal List<byte> Setters { get; } = [];
        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint code,
            byte[] buffer, CancellationToken caller, CancellationToken deadline)
        {
            if (code == 0x000B0191)
            {
                request = buffer.ToArray();
                Commands.Add((request[7], request[8]));
                if (request[7] == 0x07 && request[8] == 0x12)
                {
                    Raw = request[9];
                    Setters.Add(Raw);
                }
            }
            else
            {
                request.CopyTo(buffer, 0);
                buffer[1] = 2;
                if (buffer[7] == 0x07 && buffer[8] == 0x92)
                {
                    buffer[9] = Raw;
                }
                else if (buffer[7] == 0x07 && buffer[8] == 0x12 && FailNextSetterResponse)
                {
                    buffer[9] ^= 1;
                    FailNextSetterResponse = false;
                }
                else if (buffer[7] == 0x0D && buffer[8] == 0x82)
                {
                    buffer[11] = Mode;
                }
                else if (buffer[7] == 0x03 && buffer[8] == 0x83)
                {
                    buffer[11] = 128;
                }
                else if (buffer[7] == 0x0F && buffer[8] == 0x82)
                {
                    buffer[11] = Effect;
                }
                else if (buffer[7] == 0x03 && buffer[8] == 0x80)
                {
                    AfterLogoRead?.Invoke();
                }
                buffer[89] = 0;
                for (int index = 3; index < 89; index++)
                {
                    buffer[89] ^= buffer[index];
                }
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }
    }
}

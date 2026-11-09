using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class FanStopSettingsTests
{
    [Fact]
    public void DefaultsPreserveAutoPolicyAndDoNotArmAutomation()
    {
        FanStopSettings settings = FanStopSettings.Default;
        Assert.Equal(FanStopEndMode.Auto, settings.EndMode);
        Assert.False(settings.AutomaticEnabled);
        Assert.Equal(50, settings.AutomaticCpuBelowCelsius);
        Assert.Equal(50, settings.AutomaticGpuBelowCelsius);
        Assert.Equal(30, settings.SecondsBelowLimits);
        Assert.Equal(65, settings.CpuImmediateCutoffCelsius);
        Assert.Equal(65, settings.GpuImmediateCutoffCelsius);
        Assert.True(FanStopSettings.IsWellFormed(settings));
    }

    [Fact]
    public async Task SettingsAndResumeRoundTripAsTypedRequests()
    {
        var settings = new FanStopSettings
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = 65,
            GpuLimitCelsius = 67,
            CpuImmediateCutoffCelsius = 75,
            GpuImmediateCutoffCelsius = 77,
            SecondsAboveLimit = 11,
            AutomaticEnabled = true,
        };
        ServiceRequest[] requests =
        [
            new SetFanStopSettingsRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), settings),
            new ResumeAutomaticFanStopRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
        ];
        foreach (ServiceRequest request in requests)
        {
            using var stream = new MemoryStream();
            await IpcMessageCodec.WriteRequestAsync(stream, request, default);
            stream.Position = 0;
            Assert.Equal(request, await IpcMessageCodec.ReadRequestAsync(stream, default));
        }
    }

    [Theory]
    [InlineData(50, 50, 55, 55, true)]
    [InlineData(55, 50, 55, 55, false)]
    [InlineData(50, 56, 55, 55, false)]
    [InlineData(61, 61, 65, 65, true)]
    public void EntryMustBeStrictlyLowerThanCorrespondingCustomExit(
        int startCpu, int startGpu, int endCpu, int endGpu, bool valid)
    {
        var settings = new FanStopSettings
        {
            EndMode = FanStopEndMode.Custom,
            AutomaticEnabled = true,
            AutomaticCpuBelowCelsius = startCpu,
            AutomaticGpuBelowCelsius = startGpu,
            CpuLimitCelsius = endCpu,
            GpuLimitCelsius = endGpu,
        };
        Assert.Equal(valid, FanStopSettings.IsWellFormed(settings));
        Assert.True(FanStopSettings.IsWellFormed(settings with { AutomaticEnabled = false }));
    }

    [Fact]
    public async Task MalformedSettingsNeverReachRequestDispatch()
    {
        FanStopSettings[] invalid =
        [
            new() { EndMode = (FanStopEndMode)99 },
            new() { CpuLimitCelsius = 101 },
            new() { GpuLimitCelsius = 9 },
            new() { CpuImmediateCutoffCelsius = 54 },
            new() { GpuImmediateCutoffCelsius = 54 },
            new() { CpuImmediateCutoffCelsius = 101 },
            new() { GpuImmediateCutoffCelsius = 9 },
            new() { SecondsAboveLimit = 0 },
            new() { SecondsBelowLimits = int.MaxValue },
            new() { AutomaticEnabled = true, AutomaticCpuBelowCelsius = 55 },
        ];
        foreach (FanStopSettings settings in invalid)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteRequestAsync(stream,
                new SetFanStopSettingsRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), settings), default));
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public void EqualCutoffIsValidWithoutChangingSelectedLimits()
    {
        var settings = new FanStopSettings
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = 100,
            GpuLimitCelsius = 99,
            CpuImmediateCutoffCelsius = 100,
            GpuImmediateCutoffCelsius = 99,
        };
        Assert.True(FanStopSettings.IsWellFormed(settings));
        Assert.False(FanStopSettings.IsWellFormed(settings with { GpuImmediateCutoffCelsius = 98 }));
    }
}

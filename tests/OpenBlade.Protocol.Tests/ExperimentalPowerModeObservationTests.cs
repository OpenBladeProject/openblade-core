using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, 0, false, false, false)]
    [InlineData(0x029F, 4, true, false, false)]
    [InlineData(0x029F, 11, false, true, true)]
    [InlineData(0x029F, 15, true, true, true)]
    [InlineData(0x02A0, 0, false, false, false)]
    [InlineData(0x02A0, 4, true, false, false)]
    [InlineData(0x02A0, 11, false, true, true)]
    [InlineData(0x02A0, 15, true, true, true)]
    public async Task SharedPowerModeFlags_AreDiagnosticObservationsWithoutFullChargeAdmission(
        int pid, byte flags, bool temporaryFullCharge, bool cpuBoost, bool localDimming)
    {
        var session = new Session { Mode = 4, PowerModeControlFlags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);

        await port.QueryPerformanceFanStateAsync(CancellationToken.None);

        var observation = Assert.Single(
            policy.Snapshot().Observations,
            item => item.Operation == "MaximumFans" && item.Outcome == "Confirmed");
        Assert.Contains($"TemporaryFullChargeBit={temporaryFullCharge}", observation.ConfirmedValue!);
        Assert.Contains($"CpuBoostBit={cpuBoost}", observation.ConfirmedValue!);
        Assert.Contains($"LocalDimmingBit={localDimming}", observation.ConfirmedValue!);
        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        if (pid == 0x02A0)
        {
            Assert.Equal(temporaryFullCharge, await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
            Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
        }
        else
        {
            Assert.Null(await port.QueryTemporaryFullChargeAsync(CancellationToken.None));
        }
        Assert.Empty(session.Writes);
        Assert.Equal(pid == 0x02A0 ? 2 : 1, session.Commands.Count(command => command.ClassId == 0x07 && command.CommandId == 0x8F));
    }
}

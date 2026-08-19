using System.Security.Cryptography;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ReviewedHyperBoostPowerSourceBoundaryPlanTests
{
    [Fact]
    public void LoadTrusted_BindsUsbCAndBatteryAutoExitContract()
    {
        ReviewedHyperBoostPowerSourceBoundaryPlan plan =
            ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();

        Assert.Equal(
            "rz09-0581-bios-4.00-hyperboost-power-source-boundary-v1",
            plan.PlanId);
        Assert.Equal(
            ReviewedHyperBoostPowerSourceBoundaryPlan.TrustedSha256,
            plan.SourceSha256);
        Assert.Equal(
            [AdapterPowerState.FullPower280W, AdapterPowerState.FullPower330W],
            plan.InitialAdapterStates);
        Assert.Equal(
            [AdapterPowerState.UsbCPower100W, AdapterPowerState.Battery],
            plan.DestinationAdapterStates);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.ObservationDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(500), plan.SampleInterval);
        Assert.Equal(3, plan.RequiredConsecutiveAutomaticSamples);
        Assert.True(plan.ForbidOpenBladeDisableDuringObservation);
        Assert.True(plan.RequireFullPowerReconnectBeforeCleanup);

        int maximumSamples = checked((int)(plan.ObservationDuration.Ticks /
            plan.SampleInterval.Ticks));
        const int baselineAndCandidateTransactions = 9;
        const int transactionsPerObservation = 3;
        const int reconnectAndMaximumCleanupTransactions = 7;
        Assert.True(
            baselineAndCandidateTransactions
            + maximumSamples * transactionsPerObservation
            + reconnectAndMaximumCleanupTransactions <= byte.MaxValue);
    }

    [Theory]
    [InlineData(
        "\"destinationAdapterStates\": [\"usbCPower100W\", \"battery\"]",
        "\"destinationAdapterStates\": [\"usbCPower100W\"]")]
    [InlineData(
        "\"requiredConsecutiveAutomaticSamples\": 3",
        "\"requiredConsecutiveAutomaticSamples\": 1")]
    [InlineData(
        "\"forbidOpenBladeDisableDuringObservation\": true",
        "\"forbidOpenBladeDisableDuringObservation\": false")]
    [InlineData(
        "\"requireFullPowerReconnectBeforeCleanup\": true",
        "\"requireFullPowerReconnectBeforeCleanup\": false")]
    [InlineData("\"biosVersion\": \"4.00\"", "\"biosVersion\": \"3.01\"")]
    public void Parse_RejectsChangedSafetyContract(string oldValue, string newValue)
    {
        byte[] json = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(TrustedJson()).Replace(
                oldValue,
                newValue,
                StringComparison.Ordinal));

        Assert.Throws<UnvalidatedCommandException>(() =>
            ReviewedHyperBoostPowerSourceBoundaryPlan.Parse(
                json,
                Convert.ToHexString(SHA256.HashData(json))));
    }

    private static byte[] TrustedJson() => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Rz09_0581_02e0_4_00_HyperBoostPowerSourceBoundaryPlan.json"));
}

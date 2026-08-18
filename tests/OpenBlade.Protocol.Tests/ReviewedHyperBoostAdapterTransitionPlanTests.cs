using System.Security.Cryptography;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ReviewedHyperBoostAdapterTransitionPlanTests
{
    [Fact]
    public void LoadTrusted_BindsDedicatedAdapterTransitionContract()
    {
        ReviewedHyperBoostAdapterTransitionPlan plan =
            ReviewedHyperBoostAdapterTransitionPlan.LoadTrusted();

        Assert.Equal(
            "rz09-0581-bios-4.00-pad-absent-adapter-transition-v1",
            plan.PlanId);
        Assert.Equal(ReviewedHyperBoostAdapterTransitionPlan.TrustedSha256,
            plan.SourceSha256);
        Assert.Equal(
            [AdapterPowerState.FullPower280W, AdapterPowerState.FullPower330W],
            plan.InitialAdapterStates);
        Assert.Equal(AdapterPowerState.UsbCPower100W,
            plan.DestinationAdapterState);
        Assert.True(plan.RequirePadAbsenceAfterSwap);
        Assert.True(plan.RestoreBalancedImmediately);
    }

    [Theory]
    [InlineData("\"destinationAdapterState\": \"usbCPower100W\"", "\"destinationAdapterState\": \"fullPower280W\"")]
    [InlineData("\"requirePadAbsenceAfterSwap\": true", "\"requirePadAbsenceAfterSwap\": false")]
    [InlineData("\"restoreBalancedImmediately\": true", "\"restoreBalancedImmediately\": false")]
    [InlineData("\"biosVersion\": \"4.00\"", "\"biosVersion\": \"3.01\"")]
    public void Parse_RejectsChangedSafetyContract(string oldValue, string newValue)
    {
        byte[] json = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(TrustedJson()).Replace(
                oldValue,
                newValue,
                StringComparison.Ordinal));

        Assert.Throws<UnvalidatedCommandException>(() =>
            ReviewedHyperBoostAdapterTransitionPlan.Parse(
                json,
                Convert.ToHexString(SHA256.HashData(json))));
    }

    private static byte[] TrustedJson() => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostAdapterTransitionPlan.json"));
}

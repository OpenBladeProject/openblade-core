using System.Security.Cryptography;
using System.Text;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ReviewedHyperBoostThermalPlanTests
{
    [Fact]
    public void LoadTrustedPadAbsentNoLoad_UsesPinnedEmbeddedPlan()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();

        Assert.Equal(
            "rz09-0581-bios-4.00-pad-absent-no-load-dwell-v1",
            plan.PlanId);
        Assert.Equal(
            ReviewedHyperBoostThermalPlan.TrustedPadAbsentNoLoadSha256,
            plan.SourceSha256);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), plan.SampleInterval);
        Assert.Equal(85, plan.MaximumCpuTemperatureCelsius);
        Assert.Equal(80, plan.MaximumGpuTemperatureCelsius);
        Assert.Equal(HyperBoostGpuSensorPolicy.AllowInactive, plan.GpuSensorPolicy);
        Assert.Equal(
            HyperBoostValidationWorkloadKind.SustainedNoLoadDwell,
            plan.WorkloadKind);
    }

    [Fact]
    public void Parse_AcceptsOnlyExactHashPinnedReviewedPlan()
    {
        byte[] json = Json();
        string hash = Convert.ToHexString(SHA256.HashData(json));

        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.Parse(json, hash);

        Assert.Equal("rz09-0581-pad-absent-sustained-v1", plan.PlanId);
        Assert.Equal("RZ09-0581", plan.ModelNumber);
        Assert.Equal("4.00", plan.BiosVersion);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), plan.SampleInterval);
        Assert.Equal(hash, plan.SourceSha256);
    }

    [Fact]
    public void Parse_RejectsHashMismatchAndUnknownFields()
    {
        byte[] json = Json();
        Assert.Throws<UnvalidatedCommandException>(() =>
            ReviewedHyperBoostThermalPlan.Parse(json, new string('A', 64)));

        byte[] withUnknown = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(json).Replace(
                "\"workloadSha256\":null",
                "\"workloadSha256\":null,\"operatorLimit\":95",
                StringComparison.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(withUnknown));
        Assert.Throws<UnvalidatedCommandException>(() =>
            ReviewedHyperBoostThermalPlan.Parse(withUnknown, hash));
    }

    [Theory]
    [InlineData("\"modelNumber\":\"RZ09-0581\"", "\"modelNumber\":\"RZ09-0528\"")]
    [InlineData("\"biosVersion\":\"4.00\"", "\"biosVersion\":\"3.01\"")]
    [InlineData("\"durationSeconds\":60", "\"durationSeconds\":0")]
    [InlineData("\"sampleIntervalMilliseconds\":2000", "\"sampleIntervalMilliseconds\":7000")]
    [InlineData("\"sampleIntervalMilliseconds\":2000", "\"sampleIntervalMilliseconds\":1")]
    [InlineData("\"durationSeconds\":60", "\"durationSeconds\":98")]
    [InlineData("\"maximumCpuTemperatureCelsius\":90", "\"maximumCpuTemperatureCelsius\":0")]
    [InlineData("\"maximumCpuTemperatureCelsius\":90", "\"maximumCpuTemperatureCelsius\":116")]
    [InlineData("\"maximumGpuTemperatureCelsius\":85", "\"maximumGpuTemperatureCelsius\":126")]
    [InlineData("\"minimumFan1Rpm\":2000", "\"minimumFan1Rpm\":-1")]
    public void Parse_RejectsStructurallyInvalidPlan(string oldValue, string newValue)
    {
        byte[] json = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Json()).Replace(
                oldValue,
                newValue,
                StringComparison.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(json));

        Assert.Throws<UnvalidatedCommandException>(() =>
            ReviewedHyperBoostThermalPlan.Parse(json, hash));
    }

    [Fact]
    public void Parse_AcceptsBoundedExternalLoadContractButTrustedRouteFailsClosed()
    {
        byte[] json = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Json())
                .Replace(
                    "\"workloadKind\":\"sustainedNoLoadDwell\"",
                    "\"workloadKind\":\"externalReviewedLoad\"",
                    StringComparison.Ordinal)
                .Replace(
                    "\"workloadName\":null",
                    "\"workloadName\":\"reviewed-workload.exe\"",
                    StringComparison.Ordinal)
                .Replace(
                    "\"workloadSha256\":null",
                    $"\"workloadSha256\":\"{new string('B', 64)}\"",
                    StringComparison.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(json));

        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.Parse(json, hash);

        Assert.Equal(
            HyperBoostValidationWorkloadKind.ExternalReviewedLoad,
            plan.WorkloadKind);
        Assert.Equal("reviewed-workload.exe", plan.WorkloadName);
        Assert.Equal(new string('B', 64), plan.WorkloadSha256);
        Assert.Throws<UnvalidatedCommandException>(
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentExternalLoad);
    }

    private static byte[] Json() => Encoding.UTF8.GetBytes(
        """
        {"planId":"rz09-0581-pad-absent-sustained-v1","modelNumber":"RZ09-0581","biosVersion":"4.00","durationSeconds":60,"sampleIntervalMilliseconds":2000,"maximumCpuTemperatureCelsius":90,"maximumGpuTemperatureCelsius":85,"gpuSensorPolicy":"required","minimumFan1Rpm":2000,"minimumFan2Rpm":2000,"workloadKind":"sustainedNoLoadDwell","workloadName":null,"workloadSha256":null}
        """);
}

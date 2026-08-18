using System.Security.Cryptography;
using System.Text;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class ExternallyLaunchedHyperBoostWorkloadSourceTests
{
    [Fact]
    public async Task ObserveAsync_MissingExactPidFailsClosedWithoutLaunchingAnything()
    {
        ReviewedHyperBoostThermalPlan plan = ExternalPlan();
        var source = new ExternallyLaunchedHyperBoostWorkloadSource(int.MaxValue);

        HyperBoostExternalWorkloadObservation observation =
            await source.ObserveAsync(plan, default);

        Assert.Equal(HyperBoostExternalWorkloadObservation.NotRunning, observation);
    }

    private static ReviewedHyperBoostThermalPlan ExternalPlan()
    {
        string json = $$"""
            {"planId":"test-external-load-v1","modelNumber":"RZ09-0581","biosVersion":"4.00","durationSeconds":60,"sampleIntervalMilliseconds":2000,"maximumCpuTemperatureCelsius":90,"maximumGpuTemperatureCelsius":85,"gpuSensorPolicy":"required","minimumFan1Rpm":2000,"minimumFan2Rpm":2000,"workloadKind":"externalReviewedLoad","workloadName":"reviewed-workload.exe","workloadSha256":"{{new string('B', 64)}}"}
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return ReviewedHyperBoostThermalPlan.Parse(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}

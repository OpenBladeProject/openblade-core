using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CpuGetterUsbContinuityTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("removed")]
    [InlineData("problem")]
    [InlineData("error")]
    [InlineData("dispose")]
    public void ExactDevnodeContinuityCannotRenewAfterDrift(string failure)
    {
        const string instance = @"USB\VID_1532&PID_02C6\exact";
        var node = new CpuGetterUsbNode(instance, 8, 0);
        bool throws = false; int releases = 0;
        using var proof = new CpuGetterUsbContinuity(() => throws ? throw new IOException() : node,
            instance, () => releases++);
        Assert.True(proof.IsCurrent());
        switch (failure)
        {
            case "identity": node = node with { InstanceId = instance + "other" }; break;
            case "removed": node = node with { Status = 0 }; break;
            case "problem": node = node with { Problem = 1 }; break;
            case "error": throws = true; break;
            case "dispose": proof.Dispose(); break;
        }
        Assert.False(proof.IsCurrent());
        node = new(instance, 8, 0); throws = false;
        Assert.False(proof.IsCurrent()); proof.Dispose(); Assert.Equal(1, releases);
    }
}

using System.Runtime.InteropServices;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapWorkerJobTests
{
    [Fact]
    public void ExtendedLimitInformationMatchesThePinnedX64Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(48, Marshal.SizeOf<SnapTapWorkerJob.IoCounters>());
        Assert.Equal(64, Marshal.SizeOf<SnapTapWorkerJob.JobObjectBasicLimitInformation>());
        Assert.Equal(144, Marshal.SizeOf<SnapTapWorkerJob.JobObjectExtendedLimitInformation>());

        Assert.Equal(16, OffsetOf<SnapTapWorkerJob.JobObjectBasicLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectBasicLimitInformation.LimitFlags)));
        Assert.Equal(24, OffsetOf<SnapTapWorkerJob.JobObjectBasicLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectBasicLimitInformation.MinimumWorkingSetSize)));
        Assert.Equal(48, OffsetOf<SnapTapWorkerJob.JobObjectBasicLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectBasicLimitInformation.Affinity)));
        Assert.Equal(64, OffsetOf<SnapTapWorkerJob.JobObjectExtendedLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectExtendedLimitInformation.IoInfo)));
        Assert.Equal(112, OffsetOf<SnapTapWorkerJob.JobObjectExtendedLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectExtendedLimitInformation.ProcessMemoryLimit)));
        Assert.Equal(136, OffsetOf<SnapTapWorkerJob.JobObjectExtendedLimitInformation>(
            nameof(SnapTapWorkerJob.JobObjectExtendedLimitInformation.PeakJobMemoryUsed)));
    }

    [Fact]
    public void PolicyContainsOnlyKillOnCloseAndClearsHandleInheritance()
    {
        SnapTapWorkerJob.JobObjectExtendedLimitInformation limits =
            SnapTapWorkerJob.CreateKillOnCloseLimits();

        Assert.Equal(9, SnapTapWorkerJob.JobObjectExtendedLimitInformationClass);
        Assert.Equal(0x00002000u, SnapTapWorkerJob.JobObjectLimitKillOnJobClose);
        Assert.Equal(0x00000001u, SnapTapWorkerJob.HandleFlagInherit);
        Assert.Equal(SnapTapWorkerJob.JobObjectLimitKillOnJobClose,
            limits.BasicLimitInformation.LimitFlags);
        Assert.Equal(0u, limits.BasicLimitInformation.ActiveProcessLimit);
        Assert.Equal((nuint)0, limits.ProcessMemoryLimit);
        Assert.Equal((nuint)0, limits.JobMemoryLimit);
    }

    private static int OffsetOf<T>(string field) where T : struct =>
        checked((int)Marshal.OffsetOf<T>(field));
}

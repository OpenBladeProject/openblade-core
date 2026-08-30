using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ScopedThermalExecutionOptionsTests
{
    [Fact]
    public void ValidateFor_PerformanceRequiresExactPerformanceComponent()
    {
        var valid = new ScopedThermalExecutionOptions(
            [DeviceSubsystem.Performance],
            ScopedThermalFreshSessionRetryPolicy.BlockDuringBoundedCleanup);

        valid.ValidateFor(DeviceSubsystem.Performance);

        var coupled = new ScopedThermalExecutionOptions(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans]);
        Assert.Throws<ArgumentException>(
            () => coupled.ValidateFor(DeviceSubsystem.Performance));
    }

    [Fact]
    public void ValidateFor_FansKeepsGuardAndRetryPolicyCoupledToThermalExecution()
    {
        static string? AdditionalBlock() => "Injected submission-time block.";
        var options = new ScopedThermalExecutionOptions(
            [DeviceSubsystem.Fans, DeviceSubsystem.Performance],
            ScopedThermalFreshSessionRetryPolicy.BlockDuringBoundedCleanup,
            AdditionalBlock);

        options.ValidateFor(DeviceSubsystem.Fans);

        Assert.Equal(
            ScopedThermalFreshSessionRetryPolicy.BlockDuringBoundedCleanup,
            options.FreshSessionRetryPolicy);
        Assert.Equal(
            "Injected submission-time block.",
            options.AdditionalWriteBlockReason?.Invoke());
    }

    [Fact]
    public void ValidateFor_RejectsNonThermalAndMismatchedRoots()
    {
        var fans = new ScopedThermalExecutionOptions([DeviceSubsystem.Fans]);

        Assert.Throws<ArgumentException>(
            () => fans.ValidateFor(DeviceSubsystem.Performance));
        Assert.Throws<ArgumentException>(
            () => fans.ValidateFor(DeviceSubsystem.Battery));
    }

    [Fact]
    public void Constructor_RejectsEmptyDuplicateAndNonThermalComponents()
    {
        Assert.Throws<ArgumentException>(() => new ScopedThermalExecutionOptions([]));
        Assert.Throws<ArgumentException>(() => new ScopedThermalExecutionOptions(
            [DeviceSubsystem.Fans, DeviceSubsystem.Fans]));
        Assert.Throws<ArgumentException>(() => new ScopedThermalExecutionOptions(
            [DeviceSubsystem.Fans, DeviceSubsystem.Battery]));
    }
}

using OpenBlade.Platform;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class PawnIoPrerequisiteTests
{
    private static PawnIoInstallation Trusted(bool started = true) => new(new Version(2, 3, 0, 0), started, "verified-catalog");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibleExistingDriverIsReusedWithoutInstallEvenWhenStopped(bool started)
    {
        FakeOperations operations = new() { State = Trusted(started) };
        Assert.Equal(!started, await new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(0, operations.Installs);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3010, true)]
    public async Task OfficialExitCodePreservesRebootMeaningAndReadsBackOrdinarySuccess(int code, bool reboot)
    {
        FakeOperations operations = new() { ExitCode = code, InstalledState = Trusted() };
        Assert.Equal(reboot, await new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(1, operations.Installs);
        Assert.Equal(1, operations.Inspections);
    }

    [Fact]
    public async Task SuccessfulExitWithoutBindingFailsWithoutReplay()
    {
        FakeOperations operations = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(1, operations.Installs);
    }

    [Fact]
    public async Task SuccessfulExitWithStoppedTrustedBindingReportsRebootWithoutReinstall()
    {
        FakeOperations operations = new() { InstalledState = Trusted(started: false) };
        Assert.True(await new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(1, operations.Installs);
    }

    [Fact]
    public async Task RebootRequiredDoesNotPretendBindingExists()
    {
        FakeOperations operations = new() { ExitCode = 3010 };
        Assert.True(await new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Null(operations.State);
        Assert.Equal(1, operations.Inspections);
    }

    [Fact]
    public async Task DurableRebootPendingInspectionDoesNotSubmitAnotherInstaller()
    {
        FakeOperations operations = new() { RebootPending = true };
        Assert.True(await new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(0, operations.Installs);
        Assert.Equal(1, operations.Inspections);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1603)]
    public async Task InstallerFailureRetainsExitCodeAndDoesNotRetry(int code)
    {
        FakeOperations operations = new() { ExitCode = code };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Contains(code.ToString(), error.Message);
        Assert.Equal(1, operations.Installs);
    }

    [Fact]
    public async Task UntrustedOrUnsupportedExistingBindingCannotTriggerReplacement()
    {
        FakeOperations operations = new() { InspectError = new InvalidDataException("untrusted") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PawnIoPrerequisite(operations).EnsureAsync("staging", default));
        Assert.Equal(0, operations.Installs);
    }

    [Fact]
    public void InstallerPlanUsesOnlyFixedArgumentsWithoutShellExecution()
    {
        var plan = WindowsPawnIoSetupOperations.CreateProcessPlan(@"C:\Program Files\OpenBlade.installing.0123456789abcdef0123456789abcdef\PawnIO_setup.exe");
        Assert.Equal(["-install", "-silent"], plan.ArgumentList);
        Assert.False(plan.UseShellExecute);
        Assert.True(plan.CreateNoWindow);
    }

    private sealed class FakeOperations : IPawnIoSetupOperations
    {
        public PawnIoInstallation? State { get; set; }
        public PawnIoInstallation? InstalledState { get; init; }
        public int ExitCode { get; init; }
        public Exception? InspectError { get; init; }
        public bool RebootPending { get; init; }
        public int Installs { get; private set; }
        public int Inspections { get; private set; }
        public PawnIoSetupInspection Inspect(CancellationToken cancellationToken)
        {
            Inspections++;
            if (InspectError is not null) { throw InspectError; }
            return new(State, RebootPending);
        }
        public Task<PawnIoInstallResult> InstallAsync(string installer, CancellationToken cancellationToken)
        {
            Installs++;
            State = InstalledState;
            return Task.FromResult(new PawnIoInstallResult(ExitCode, InstalledState));
        }
    }
}

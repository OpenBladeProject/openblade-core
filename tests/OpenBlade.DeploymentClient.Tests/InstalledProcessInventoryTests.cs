using System.Diagnostics;
using System.Security.Principal;
using OpenBlade.DeploymentClient;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class InstalledProcessInventoryTests
{
    private static readonly InstalledPayloadPaths Paths = new(@"C:\Program Files\OpenBlade",
        @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe", @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe");
    private const string Owner = "S-1-5-21-123";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ValidFamilies_CountOnePrimaryAndRetainHelpers(bool snapTap, bool thx)
    {
        List<InstalledProcessObservation> processes = [Agent(10), Tray()];
        if (snapTap)
        {
            processes.AddRange([Agent(11, "--snap-tap-input", "10"), Agent(12, "--snap-tap-driver-worker", "11")]);
        }

        if (thx)
        {
            processes.Add(Agent(13, "--thx-audio-helper", "OpenBlade.Thx.Helper.v2.10.0123456789abcdef0123456789abcdef", "10"));
        }

        InstalledProcessInventory result = Evaluate(processes);
        Assert.True(result.HasExactFinalState);
        Assert.Equal(1, result.SessionAgentCount);
        Assert.Equal((snapTap ? 2 : 0) + (thx ? 1 : 0), result.SessionAgentHelperCount);
        Assert.True(result.TryGetRestorableShape(out InstalledProcessShape shape));
        Assert.Equal(InstalledProcessShape.SessionAgentAndTray, shape);
    }

    [Fact]
    public void HelpersCannotSatisfyEmptyShutdownOrRestorablePrimaryShape()
    {
        InstalledProcessInventory orphan = Evaluate([Agent(11, "--snap-tap-input", "10")]);
        Assert.False(orphan.IsUnambiguous);
        Assert.False(orphan.IsEmpty);
        Assert.False(orphan.TryGetRestorableShape(out _));
        Assert.False(new InstalledProcessInventory(true, 0, 0, 1).IsEmpty);
        Assert.False(new InstalledProcessInventory(true, 0, 0, 1).TryGetRestorableShape(out _));
        Assert.True(Evaluate([]).IsEmpty);
    }

    [Fact]
    public void InstalledStartupShortcut_IsStillOnePrimaryTray()
    {
        Assert.True(Evaluate([Agent(10), Tray() with { Arguments = [Paths.TrayExecutable, "--startup"] }]).HasExactFinalState);
        Assert.False(Evaluate([Agent(10), Tray() with { Arguments = [Paths.TrayExecutable, "--startup", "extra"] }]).IsUnambiguous);
    }

    [Theory]
    [InlineData("--unknown", "10")]
    [InlineData("--snap-tap-input", "0")]
    [InlineData("--snap-tap-input", "-10")]
    [InlineData("--snap-tap-input", "+10")]
    [InlineData("--snap-tap-input", "2147483648")]
    [InlineData("--snap-tap-input", "10 extra")]
    [InlineData("--snap-tap-driver-worker", "10")]
    public void UnknownMalformedOrWrongRoleParent_Blocks(string mode, string parent)
    {
        Assert.False(Evaluate([Agent(10), Agent(11, mode, parent)]).IsUnambiguous);
    }

    [Fact]
    public void InvalidThxNonceOrParentBinding_Blocks()
    {
        Assert.False(Evaluate([Agent(10), Agent(11, "--thx-audio-helper", "OpenBlade.Thx.Helper.v2.10.invalid", "10")]).IsUnambiguous);
        Assert.False(Evaluate([Agent(10), Agent(11, "--thx-audio-helper", "OpenBlade.Thx.Helper.v2.12.0123456789abcdef0123456789abcdef", "10")]).IsUnambiguous);
    }

    [Fact]
    public void DuplicatePrimariesHelpersOrPids_Block()
    {
        Assert.False(Evaluate([Agent(10), Agent(11)]).IsUnambiguous);
        Assert.False(Evaluate([Agent(10), Agent(11, "--snap-tap-input", "10"), Agent(12, "--snap-tap-input", "10")]).IsUnambiguous);
        Assert.False(Evaluate([Agent(10), Agent(10)]).IsUnambiguous);
        Assert.False(Evaluate([Agent(10), Tray(), Tray() with { ProcessId = 21 }]).IsUnambiguous);
    }

    [Fact]
    public void ForeignOwnerSessionIntegrityMissingDataOrCreationRace_Block()
    {
        InstalledProcessObservation helper = Agent(11, "--snap-tap-input", "10");
        foreach (InstalledProcessObservation invalid in new[]
        {
            helper with { OwnerSid = "S-1-5-21-456" }, helper with { OwnerSid = null },
            helper with { SessionId = 2 }, helper with { IsNormalIntegrity = false },
            helper with { CreationTime = 9 }, helper with { CreationTime = 10 },
            helper with { ParentProcessId = 100 }, helper with { Arguments = [] },
            helper with { Arguments = [@"C:\Other\OpenBlade.SessionAgent.exe", "--snap-tap-input", "10"] },
        })
        {
            Assert.False(Evaluate([Agent(10), invalid]).IsUnambiguous);
        }
        Assert.False(InstalledProcessFamilyPolicy.Evaluate(Paths, 1, null, new(true, [Agent(10)])).IsUnambiguous);
    }

    [Fact]
    public void InaccessibleOrChangingSnapshot_CannotProveAbsenceOrFinalState()
    {
        Assert.False(Evaluate([], complete: false).IsEmpty);
        Assert.False(Evaluate([Agent(10), Tray()], complete: false).HasExactFinalState);
        Assert.False(Evaluate([Agent(10), Tray() with { Arguments = [Paths.TrayExecutable, "--helper"] }]).IsUnambiguous);
    }

    [Fact]
    public void UnrelatedImageIsIgnored_ButExactImageInOtherSessionIsNot()
    {
        Assert.True(Evaluate([Agent(10) with { ExecutablePath = @"C:\Other\OpenBlade.SessionAgent.exe" }]).IsEmpty);
        Assert.False(Evaluate([Agent(10) with { SessionId = 2 }]).IsEmpty);
    }

    [Fact]
    public void NativeReader_ReadsCurrentProcessWithoutLaunchingInstalledCode()
    {
        using Process current = Process.GetCurrentProcess();
        InstalledProcessObservation observation = WindowsInstalledProcessInspection.Read(current, Environment.ProcessPath!);
        using WindowsIdentity owner = WindowsIdentity.GetCurrent();
        Assert.Equal(Environment.ProcessId, observation.ProcessId);
        Assert.True(observation.ParentProcessId > 0);
        Assert.Equal(owner.User!.Value, observation.OwnerSid);
        Assert.Equal(current.SessionId, observation.SessionId);
        Assert.True(observation.CreationTime > 0);
        Assert.NotEmpty(observation.Arguments);
        Assert.Equal(Environment.ProcessPath, observation.Arguments[0], ignoreCase: true);
    }

    [Theory]
    [InlineData("--snap-tap-input")]
    [InlineData("--snap-tap-driver-worker")]
    public void NativeReader_BindsHarmlessChildArgumentsAndActualParent(string mode)
    {
        string stopName = $"Local\\OpenBlade.InventoryFixture.{Guid.NewGuid():N}";
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        string fixture = Path.ChangeExtension(typeof(LaunchFixture).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(fixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment["OpenBlade.InventoryFixtureStop"] = stopName;
        using Process child = Process.Start(start)!;
        try
        {
            InstalledProcessObservation observation = WindowsInstalledProcessInspection.Read(child, fixture);
            Assert.Equal(Environment.ProcessId, observation.ParentProcessId);
            Assert.Equal(new[] { fixture, mode, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, observation.Arguments);
        }
        finally
        {
            stop.Set();
            Assert.True(child.WaitForExit(20000));
        }
    }

    private static InstalledProcessInventory Evaluate(IReadOnlyList<InstalledProcessObservation> processes, bool complete = true) =>
        InstalledProcessFamilyPolicy.Evaluate(Paths, 1, Owner, new(complete, processes));

    private static InstalledProcessObservation Agent(int id, params string[] args) =>
        new(id, args.Length == 0 ? 1 : int.TryParse(args[^1], out int parent) ? parent : 1,
            id, Paths.SessionAgentExecutable, 1, Owner, true, [Paths.SessionAgentExecutable, .. args]);

    private static InstalledProcessObservation Tray() => new(20, 1, 20, Paths.TrayExecutable, 1, Owner, true, [Paths.TrayExecutable]);
}

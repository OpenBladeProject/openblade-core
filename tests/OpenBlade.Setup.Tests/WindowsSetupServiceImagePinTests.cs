using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsSetupServiceImagePinTests
{
    [NonAdminSetupImagePinFact]
    public async Task RetainsOriginalImageAndAncestorAcrossFixtureReadiness()
    {
        string source = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // Measure the separate, OS-provisioned fixture source before staging.
        using var scratch = new ScratchImage(File.ReadAllBytes(source));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Process? child = null;
        try
        {
            using var retained = WindowsSetupRetainedFixtureLaunch.Launch(scratch.Expected, (image, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(scratch.Image, image);
                AssertSharingViolation(() => File.OpenWrite(image).Dispose());
                AssertDirectoryPinned(scratch.Root);
                child = Process.Start(new ProcessStartInfo(image, "/d /q /c \"echo READY&set /p hold=\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = scratch.Root,
                })!;
                return new(child.SafeHandle, (uint)child.Id, child.StartTime.ToFileTimeUtc());
            }, deadline.Token);

            SetupCandidateLaunch expected = retained.Expected;
            var verification = new SetupCandidateServiceVerification(expected, retained, token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new SetupCandidateServiceObservation(
                    expected.ProcessId, true, 0x10, 2, "LocalSystem", $"\"{scratch.Image}\""));
            });
            await verification.VerifyLaunchAsync(deadline.Token);
            Assert.Equal("READY", await child!.StandardOutput.ReadLineAsync(deadline.Token));
            await verification.VerifyLaunchAsync(deadline.Token);
            AssertSharingViolation(() => File.Move(scratch.Image, scratch.Image + "-replaced"));
            AssertDirectoryPinned(scratch.Parent);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => retained.ReadLiveAsync(new CancellationToken(canceled: true)).AsTask());
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            Assert.Null(await retained.ReadLiveAsync(deadline.Token));
            await Assert.ThrowsAsync<IOException>(() => verification.VerifyLaunchAsync(deadline.Token));
            AssertSharingViolation(() => File.OpenWrite(scratch.Image).Dispose());
            AssertDirectoryPinned(scratch.Parent);

            retained.Dispose();
            Assert.Null(await retained.ReadLiveAsync(deadline.Token));
            File.Move(scratch.Image, scratch.Image + "-released");
            Directory.Move(scratch.Root, scratch.Root + "-released");
        }
        finally
        {
            if (child is not null)
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    Assert.True(child.WaitForExit(5000));
                }
                child.Dispose();
            }
        }
    }

    [NonAdminSetupImagePinFact]
    public void RejectsSameLengthSubstitutionAndReleasesPinWithoutLaunching()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        File.WriteAllBytes(scratch.Image, [3, 2, 1]);
        var failure = Assert.Throws<IOException>(() => WindowsSetupRetainedFixtureLaunch.Launch(
            scratch.Expected, (_, _) => throw new InvalidOperationException("Unexpected launch."), default));
        Assert.Contains("digest differs", failure.Message);
        File.WriteAllBytes(scratch.Image, [1, 2, 3]);
        using var pin = WindowsSetupServiceImagePin.OpenFixture(scratch.Expected, default);
        Assert.Equal(scratch.Expected.Sha256, pin.Verify(default));
    }

    [NonAdminSetupImagePinFact]
    public void RejectsExistingWriterAndUntrustedAncestor()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        using (File.Open(scratch.Image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var failure = Assert.Throws<IOException>(
                () => WindowsSetupServiceImagePin.OpenFixture(scratch.Expected, default));
            Assert.Equal(32, Assert.IsType<System.ComponentModel.Win32Exception>(failure.InnerException).NativeErrorCode);
        }

        using (var pin = WindowsSetupServiceImagePin.OpenFixture(scratch.Expected, default))
        {
            DirectoryInfo parent = new(scratch.Parent);
            DirectorySecurity access = parent.GetAccessControl();
            access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            parent.SetAccessControl(access);
            Assert.Throws<UnauthorizedAccessException>(() => pin.Verify(default));
        }
        Assert.Throws<UnauthorizedAccessException>(
            () => WindowsSetupServiceImagePin.OpenFixture(scratch.Expected, default));
    }

    [NonAdminSetupImagePinFact]
    public void CancelsBeforeHandoffAndRejectsLengthAndScopeMismatch()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        Assert.Throws<OperationCanceledException>(() => WindowsSetupRetainedFixtureLaunch.Launch(
            scratch.Expected, (_, _) => throw new InvalidOperationException("Unexpected launch."), new(true)));
        Assert.Throws<ArgumentException>(
            () => WindowsSetupServiceImagePin.OpenFixture(scratch.Expected with { Directory = scratch.Parent }, default));
        Assert.Throws<ArgumentException>(
            () => WindowsSetupServiceImagePin.OpenFixture(scratch.Expected with { Length = 64 * 1024 * 1024 + 1 }, default));
        var failure = Assert.Throws<IOException>(
            () => WindowsSetupServiceImagePin.OpenFixture(scratch.Expected with { Length = 2 }, default));
        Assert.Contains("length differs", failure.Message);
    }

    [NonAdminSetupImagePinFact]
    public void RejectsWrongProcessHandoffAndReleasesTheImage()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        using Process wrongProcess = Process.GetCurrentProcess();
        var failure = Assert.Throws<IOException>(() => WindowsSetupRetainedFixtureLaunch.Launch(scratch.Expected,
            (_, _) => new(wrongProcess.SafeHandle, (uint)wrongProcess.Id, wrongProcess.StartTime.ToFileTimeUtc()), default));
        Assert.Contains("original live candidate launch", failure.Message);
        File.Move(scratch.Image, scratch.Image + "-released");
    }

    private static void AssertSharingViolation(Action action) =>
        Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(action).HResult);

    private static void AssertDirectoryPinned(string path)
    {
        string moved = path + "-moved";
        try
        {
            AssertSharingViolation(() => Directory.Move(path, moved));
        }
        finally
        {
            if (Directory.Exists(moved))
            {
                Directory.Move(moved, path);
            }
        }
    }

    private sealed class ScratchImage : IDisposable
    {
        internal readonly string Parent = Path.Combine(AppContext.BaseDirectory, $"phase9s-{Guid.NewGuid():N}");
        internal string Root => Expected.Directory;
        internal string Image => Path.Combine(Root, WindowsSetupServiceImagePin.ServiceFileName);
        internal SetupFixtureServiceBuild Expected { get; }

        internal ScratchImage(byte[] source)
        {
            Expected = new(Path.Combine(Parent, WindowsSetupServiceImagePin.FixtureDirectoryPrefix + Guid.NewGuid().ToString("N")),
                source.Length, Convert.ToHexString(SHA256.HashData(source)));
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Image, source);
        }

        public void Dispose() => Directory.Delete(Parent, recursive: true);
    }
}

internal sealed class NonAdminSetupImagePinFactAttribute : FactAttribute
{
    public NonAdminSetupImagePinFactAttribute()
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.IsSystem || new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "The Setup image-pin fixture requires a nonadministrator token.";
        }
    }
}

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using OpenBlade.Setup;
#if OPENBLADE_PACKAGE_PROOF_FIXTURE
using OpenBlade.Deployment;
#endif

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
        using var pin = WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected, default);
        Assert.Equal(scratch.Expected.Sha256, pin.Verify(default));
    }

    [NonAdminSetupImagePinFact]
    public void RejectsExistingWriterAndUntrustedAncestor()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        using (File.Open(scratch.Image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var failure = Assert.Throws<IOException>(
                () => WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected, default));
            Assert.Equal(32, Assert.IsType<System.ComponentModel.Win32Exception>(failure.InnerException).NativeErrorCode);
        }

        using (var pin = WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected, default))
        {
            DirectoryInfo parent = new(scratch.Parent);
            DirectorySecurity access = parent.GetAccessControl();
            access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            parent.SetAccessControl(access);
            Assert.Throws<UnauthorizedAccessException>(() => pin.Verify(default));
        }
        Assert.Throws<UnauthorizedAccessException>(
            () => WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected, default));
    }

    [NonAdminSetupImagePinFact]
    public void CancelsBeforeHandoffAndRejectsLengthAndScopeMismatch()
    {
        using var scratch = new ScratchImage([1, 2, 3]);
        Assert.Throws<OperationCanceledException>(() => WindowsSetupRetainedFixtureLaunch.Launch(
            scratch.Expected, (_, _) => throw new InvalidOperationException("Unexpected launch."), new(true)));
        Assert.Throws<ArgumentException>(
            () => WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected with { Directory = scratch.Parent }, default));
        Assert.Throws<ArgumentException>(
            () => WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected with { Length = 64 * 1024 * 1024 + 1 }, default));
        var failure = Assert.Throws<IOException>(
            () => WindowsSetupServiceImagePin.OpenSyntheticFixture(scratch.Expected with { Length = 2 }, default));
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

#if OPENBLADE_PACKAGE_PROOF_FIXTURE
    [NonAdminSetupImagePinFact(requireSignedBurn: true)]
    public async Task PinsTheServiceFromTheAuthenticatedSignedBurnRoleProofWithoutLaunching()
    {
        const string bundleSha256 = "cc01de2afa4b92cf4756d0565eb2baddd1a50344b900f483405badbfa8f5b5b6";
        const string revision = "af61a8c842429bc0564390dee39b466ee0da9023";
        const string version = "0.58.4.0";
        string installer = Environment.GetEnvironmentVariable("OPENBLADE_PHASE_9U_SIGNED_BURN_FIXTURE")!;
        var selected = new WindowsNativePayloadIdentity(bundleSha256, new(revision, Version.Parse(version)));
        var proof = await WindowsNativePackageRoleProof.ReadPinnedSigned9pAsync(installer);
        string roleDirectory = Environment.GetEnvironmentVariable("OPENBLADE_PHASE_9W_EXTRACTED_ROLES")!;
        byte[] service = File.ReadAllBytes(Path.Combine(roleDirectory, WindowsNativeRoleInventory.FileName(WindowsNativeRole.Service)));
        using var scratch = new ScratchImage(service);

        Assert.Equal(selected, proof.Identity);
        Assert.Equal(new WindowsNativePackageRoleProof.RoleImage(WindowsNativeRole.Service,
            "pay4L3J9dL2pH_u0Is9Kw6K3K0.0D0", @"payload\openblade-windows-service-candidate.exe", "a1", "OpenBlade.Service.exe", 337920,
            "6235dfd6b3aa304f3be525d51de05733d3678cf006547a1f94cfed367a00885c"), proof.Service);
        Assert.Equal(new WindowsNativePackageRoleProof.RoleImage(WindowsNativeRole.SessionAgent,
            "pay4XjLgXtEiNaNcGf1mmCkSd.dxfQ", @"payload\openblade-windows-agent-candidate.exe", "a2", "OpenBlade.SessionAgent.exe", 144896,
            "ddd3112e117dd8ae4c8969aba0e91b7768a69e95bdb5bb0be54974a71efe38da"), proof.SessionAgent);

        // The pin verifies staged bytes independently, with no process launch.
        using (var pin = WindowsSetupServiceImagePin.OpenPackageFixture(scratch.Root, proof, default))
        {
            Assert.Equal(scratch.Image, pin.ExecutablePath);
            Assert.Equal(proof.Service.Sha256.ToUpperInvariant(), pin.Verify(default));
            AssertSharingViolation(() => File.OpenWrite(scratch.Image).Dispose());
        }

        string agent = Path.Combine(roleDirectory, WindowsNativeRoleInventory.FileName(WindowsNativeRole.SessionAgent));
        File.Copy(agent, scratch.Image, overwrite: true);
        Assert.Contains("length differs", Assert.Throws<IOException>(() =>
            WindowsSetupServiceImagePin.OpenPackageFixture(scratch.Root, proof, default)).Message);
        service[^1] ^= 1;
        File.WriteAllBytes(scratch.Image, service);
        Assert.Contains("digest differs", Assert.Throws<IOException>(() =>
            WindowsSetupServiceImagePin.OpenPackageFixture(scratch.Root, proof, default)).Message);
    }
#endif

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
    public NonAdminSetupImagePinFactAttribute(bool requireSignedBurn = false)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.IsSystem || new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "The Setup image-pin fixture requires a nonadministrator token.";
        }
        else if (requireSignedBurn && (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBLADE_PHASE_9U_SIGNED_BURN_FIXTURE"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBLADE_PHASE_9W_EXTRACTED_ROLES"))))
        {
            Skip = "The signed Burn fixture and its extracted role directory are required.";
        }
    }
}

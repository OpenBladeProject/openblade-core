using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.Setup;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustReadOnlyHostTests
{
    [RustReadOnlyHostFact(requireProtectedFixture: true)]
    public async Task TrayClient_ProtectedRuntimeExcludesOtherWritersAndHandsBackTheAggregate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        using Stream aggregate = typeof(RustReadOnlyHostTests).Assembly
            .GetManifestResourceStream("OpenBlade.WindowsReplay.ConfigurationAggregate")!;
        using JsonDocument managedFixture = await JsonDocument.ParseAsync(aggregate, cancellationToken: token);
        byte[][] prior = ReadManagedDocuments(managedFixture.RootElement, "prior");
        byte[][] expected = ReadManagedDocuments(managedFixture.RootElement, "committed");

        using var fixture = ProtectedFixture.Create();
        string root = fixture.Root;
        string[] paths = ConfigurationNames.Select(name => Path.Combine(root, name)).ToArray();
        var managedWriter = new BatteryConfigurationStore(paths[0], paths[1], paths[2]);

        // Test-only handshake around the real managed store. The installed
        // service does not participate in this lease.
        using (fixture.CreateManagedLease())
        {
            BatteryConfiguration initial = await managedWriter.LoadAsync(token);
            Assert.True(initial.IsAuthoritative);
            Assert.Equal(80, initial.Profiles.BatteryPolicy.ChargeLimitPercent);
            AssertPrimaryBytes(root, prior);
            await AssertCompetingHostRefusedAsync(root, token);
        }
        AssertPrimaryBytes(root, prior);
        fixture.AssertTrustedRecordOwners(paths);

        string endpoint = Guid.NewGuid().ToString("N");
        byte[][] committed;
        await using (var host = DevHost.Start(endpoint, "foreground-protected-runtime", root))
        {
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));
            var initial = await client.GetStatusAsync(token);
            AssertComposedBattery(initial, saved: 80, temporary: null, confirmed: 80);
            AssertPrimaryBytes(root, prior);

            var changed = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
            var profileIdentity = ProtectedFixture.RecordIdentity(paths[0]);
            var preferencesIdentity = ProtectedFixture.RecordIdentity(paths[1]);
            // Allow preimage reads but deny replacing the commit marker. The
            // profile was already replaced when this known failure rolls back.
            using (var commitPin = new FileStream(paths[2], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(ControlOperationStatus.Failed, (await client.UpdateProfilesAsync(changed, token)).Status);
                var restored = await client.GetStatusAsync(token);
                AssertComposedBattery(restored, saved: 80, temporary: null, confirmed: 80);
                Assert.False(restored.WritesBlocked);
                AssertPrimaryBytes(root, prior);
                Assert.NotEqual(profileIdentity, ProtectedFixture.RecordIdentity(paths[0]));
                Assert.Equal(preferencesIdentity, ProtectedFixture.RecordIdentity(paths[1]));
            }
            Assert.Equal(ControlOperationStatus.Stored, (await client.UpdateProfilesAsync(changed, token)).Status);
            AssertComposedBattery(await client.GetStatusAsync(token), saved: 75, temporary: null, confirmed: 80);
            committed = paths.Select(File.ReadAllBytes).ToArray();
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected[0]), JsonNode.Parse(committed[0])));
            Assert.Equal(expected[1], committed[1]);
            Assert.Equal(preferencesIdentity, ProtectedFixture.RecordIdentity(paths[1]));
            AssertCommitHashes(committed);

            await AssertCompetingHostRefusedAsync(root, token);
            var refused = Assert.Throws<IOException>(() => fixture.OpenManagedLease().Dispose());
            Assert.Equal(unchecked((int)0x80070020), refused.HResult); // ERROR_SHARING_VIOLATION
            AssertPrimaryBytes(root, committed);
            AssertComposedBattery(await client.GetStatusAsync(token), saved: 75, temporary: null, confirmed: 80);
            await host.StopAsync(token);
        }

        string restartEndpoint = Guid.NewGuid().ToString("N");
        await using (var restart = DevHost.Start(restartEndpoint, "foreground-protected-integrated-application", root))
        {
            await restart.ExpectLineAsync("START_PENDING", token);
            await restart.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(restartEndpoint));
            await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
            var status = await ReadStatusAsync(watch, subscriptionOpened: true);
            while (status.Message != "Renderer frame in flight.")
            {
                status = await ReadStatusAsync(watch, subscriptionOpened: false);
            }
            AssertComposedBattery(status, saved: 75, temporary: null, confirmed: 80);
            var app = Assert.Single(status.Profiles.ApplicationProfiles);
            var report = new ReportApplicationContextRequest(
                OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), 1, app.ExecutablePath);
            Assert.Equal(ControlOperationStatus.Stored, (await client.ReportApplicationContextAsync(report, token)).Status);
            AssertSelection(await client.GetStatusAsync(token), app.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            await restart.AdvanceAsync(token);
            while (status.ConfirmedState?.Performance != PerformancePreset.Performance)
            {
                status = await ReadStatusAsync(watch, subscriptionOpened: false);
            }
            AssertSelection(status, app.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Performance);
            AssertPrimaryBytes(root, committed);
            await restart.RequestStopAsync(token);
            await restart.ExpectLineAsync("STOP_PENDING", token);
            var refused = Assert.Throws<IOException>(() => fixture.OpenManagedLease().Dispose());
            Assert.Equal(unchecked((int)0x80070020), refused.HResult); // ERROR_SHARING_VIOLATION
            await restart.ReleaseRendererAsync(token);
            await restart.ExpectLineAsync("STOPPED", token);
            await restart.StopAsync(token);
        }

        byte[][] handedBackBytes;
        using (fixture.OpenManagedLease())
        {
            BatteryConfiguration handedBack = await managedWriter.LoadAsync(token);
            Assert.True(handedBack.IsAuthoritative);
            Assert.Equal(75, handedBack.Profiles.BatteryPolicy.ChargeLimitPercent);
            AssertPrimaryBytes(root, committed);
            await managedWriter.SaveProfilesAsync(
                handedBack.Profiles with { BatteryPolicy = BatteryPolicy.Create(70) }, token);
            BatteryConfiguration changed = await managedWriter.LoadAsync(token);
            Assert.True(changed.IsAuthoritative);
            Assert.Equal(70, changed.Profiles.BatteryPolicy.ChargeLimitPercent);
            handedBackBytes = paths.Select(File.ReadAllBytes).ToArray();
            Assert.Equal(committed[1], handedBackBytes[1]);
            AssertCommitHashes(handedBackBytes);
        }

        byte[] fenceBytes = "fixture recovery evidence"u8.ToArray();
        string fencePath = fixture.WriteRecoveryFence(fenceBytes);
        await using var fenced = DevHost.Start(Guid.NewGuid().ToString("N"), "foreground-protected-runtime", root);
        Assert.Contains("Preserve(RecoveryRequired)", await fenced.AssertRejectedAsync(token), StringComparison.Ordinal);
        AssertPrimaryBytes(root, handedBackBytes);
        Assert.Equal(fenceBytes, File.ReadAllBytes(fencePath));
    }

    private static async Task AssertCompetingHostRefusedAsync(string root, CancellationToken token)
    {
        await using var competing = DevHost.Start(Guid.NewGuid().ToString("N"), "foreground-protected-runtime", root);
        string diagnostic = await competing.AssertRejectedAsync(token);
        Assert.Contains("protected configuration writer lease:", diagnostic, StringComparison.Ordinal);
        Assert.Contains("(os error 32)", diagnostic, StringComparison.Ordinal);
    }

    // ProfileStore.CurrentSchemaVersion is internal to the Service.
    private static readonly int CurrentProfileSchemaVersion = (int)typeof(ProfileStore)
        .GetField("CurrentSchemaVersion", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetRawConstantValue()!;

    private static void AssertCommitHashes(byte[][] aggregate)
    {
        using JsonDocument marker = JsonDocument.Parse(aggregate[2]);
        // The host commits at the managed store's current schema.
        Assert.Equal(CurrentProfileSchemaVersion, marker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(aggregate[0])),
            marker.RootElement.GetProperty("profileSha256").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(aggregate[1])),
            marker.RootElement.GetProperty("preferencesSha256").GetString());
    }

    private sealed class ProtectedFixture(string root, SafeFileHandle retained, SafeFileHandle pin) : IDisposable
    {
        private const string LeaseName = "configuration.writer-lock";
        private const string RecoveryFenceName = "battery-configuration.commit.json.recovery-required";
        private const uint BackupAndNoFollow = 0x02200000;
        private const uint ReadAttributes = 0x80;
        private const uint Delete = 0x10000;
        private const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

        private bool ownsRecoveryFence;

        internal string Root { get; } = root;

        internal static ProtectedFixture Create()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            Assert.False(identity.IsSystem);
            Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                "This opt-in fixture requires an already enabled Administrator token.");
            Assert.True(IsTrustedOwner(identity.Owner),
                $"The Windows token default owner must be Administrators or SYSTEM. Observed {identity.Owner}.");

            string driveRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            Assert.Matches(@"^[A-Za-z]:\\$", driveRoot);
            var drive = new DriveInfo(driveRoot);
            Assert.Equal(DriveType.Fixed, drive.DriveType);
            Assert.Equal("NTFS", drive.DriveFormat);

            string root = Path.Combine(driveRoot, $"OpenBlade-Phase4j-{Guid.NewGuid():N}");
            byte[] descriptor = WindowsMachineDataDirectorySecurity.CreateProtectedSecurity().GetSecurityDescriptorBinaryForm();
            GCHandle allocation = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var security = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = allocation.AddrOfPinnedObject(),
                };
                // Unlike Directory.CreateDirectory, this fails on a collision
                // and installs the Setup descriptor at creation.
                if (!CreateDirectory(root, in security))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Creating protected fixture {root}");
                }
            }
            finally
            {
                allocation.Free();
            }

            SafeFileHandle retained = CreateFile(root, ReadAttributes, ShareAll, IntPtr.Zero,
                FileMode.Open, BackupAndNoFollow, IntPtr.Zero);
            if (retained.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                retained.Dispose();
                throw new Win32Exception(error, $"Opening protected fixture {root}");
            }
            SafeFileHandle pin = OpenFixtureDirectory(root);
            try
            {
                Assert.Equal(ReadIdentity(pin), ReadIdentity(retained));
                var directory = new DirectoryInfo(root);
                WindowsMachineDataDirectorySecurity.VerifyProtectedSecurity(
                    directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
                return new ProtectedFixture(root, retained, pin);
            }
            catch
            {
                pin.Dispose();
                retained.Dispose();
                throw;
            }
        }

        internal void AssertTrustedRecordOwners(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                var owner = (SecurityIdentifier?)new FileInfo(path).GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier));
                Assert.True(IsTrustedOwner(owner), $"Managed fixture record {path} has unadmitted owner {owner}.");
            }
        }

        private static bool IsTrustedOwner(SecurityIdentifier? owner) =>
            owner is not null
            && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

        internal FileStream CreateManagedLease() => CreateProtectedLeaf(LeaseName);

        private FileStream CreateProtectedLeaf(string name) =>
            new FileInfo(Path.Combine(Root, name)).Create(
                FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write, FileShare.None,
                bufferSize: 4096, FileOptions.None, WindowsMachineDataDirectorySecurity.CreateProtectedFileSecurity());

        internal FileStream OpenManagedLease() =>
            new(Path.Combine(Root, LeaseName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        internal string WriteRecoveryFence(byte[] bytes)
        {
            using FileStream fence = CreateProtectedLeaf(RecoveryFenceName);
            ownsRecoveryFence = true;
            fence.Write(bytes);
            fence.Flush(flushToDisk: true);
            return Path.Combine(Root, RecoveryFenceName);
        }

        public void Dispose()
        {
            using (retained)
            {
                pin.Dispose();
                // Get delete access to the same object after all writer
                // processes have left. Hold it against rename while checking
                // the name and deleting only known, non-directory leaves.
                using SafeFileHandle remove = ReOpenFile(retained, ReadAttributes | Delete,
                    FileShare.ReadWrite, BackupAndNoFollow);
                var identity = ReadIdentity(remove);
                using SafeFileHandle name = CreateFile(Root, ReadAttributes, ShareAll, IntPtr.Zero,
                    FileMode.Open, BackupAndNoFollow, IntPtr.Zero);
                Assert.Equal(identity, ReadIdentity(name));

                string[] entries = Directory.GetFileSystemEntries(Root);
                Assert.All(entries, entry =>
                {
                    string leaf = Path.GetFileName(entry);
                    Assert.True(ConfigurationNames.Contains(leaf) || leaf == LeaseName
                        || ownsRecoveryFence && leaf == RecoveryFenceName,
                        $"Unexpected fixture entry retained at {entry}");
                    Assert.Equal((FileAttributes)0,
                        File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
                });
                foreach (string entry in entries)
                {
                    File.Delete(entry);
                }
                byte deleteFile = 1;
                const int fileDispositionInfo = 4;
                if (!SetFileInformationByHandle(remove, fileDispositionInfo, in deleteFile, sizeof(byte)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Retiring protected fixture {Root}");
                }
            }
            Assert.False(Directory.Exists(Root));
        }

        internal static (uint Volume, ulong Index) RecordIdentity(string path)
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadIdentity(file, directory: false);
        }

        private static (uint Volume, ulong Index) ReadIdentity(SafeFileHandle file, bool directory = true)
        {
            if (file.IsInvalid || !GetFileInformationByHandle(file, out FileInformation info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Assert.Equal(directory ? FileAttributes.Directory : 0,
                (FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint));
            return (info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccess;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWrite;
            public uint VolumeSerial;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, in SecurityAttributes attributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle ReOpenFile(
            SafeFileHandle file, uint access, FileShare share, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file, int informationClass, in byte information, int informationSize);
    }
}

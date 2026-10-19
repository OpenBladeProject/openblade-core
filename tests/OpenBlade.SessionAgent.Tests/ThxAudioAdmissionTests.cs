using System.Security.Cryptography;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioAdmissionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "OpenBlade.ThxAudioAdmission.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NativeComponentAdmissionSharesAcquisitionPins()
    {
        Assert.Equal(OpenBlade.Tray.ThxNativeComponentPackage.Sha256, WindowsThxAudioAdmissionSource.NativeDllSha256);
        Assert.Equal(OpenBlade.Tray.ThxNativeComponentPackage.FileName, WindowsThxAudioAdmissionSource.NativeDllName);
    }

    [Fact]
    public void AdmissionDefaultsDoNotAuthorizeNativeAccess()
    {
        var admission = new ThxAudioAdmission(ThxAudioAvailability.Available, null);
        Assert.Null(admission.ProductId);
        Assert.False(admission.MutationAllowed);
        Assert.False(admission.IsAvailable);
    }

    [Fact]
    public void CatalogKeepsExactModelCodecAndStackPairsSeparate()
    {
        Assert.Equal(6, ThxAudioCompatibility.Profiles.Count);
        ThxAudioCompatibilityProfile sonya12 = ThxAudioCompatibility.Profiles.Single(profile => profile.ModelNumber == "RZ09-0581");
        ThxAudioCompatibilityProfile sonya11 = ThxAudioCompatibility.Profiles.Single(profile => profile.ModelNumber == "RZ09-0528");
        ThxAudioCompatibilityProfile piper11 = ThxAudioCompatibility.Profiles.Single(profile => profile.ModelNumber == "RZ09-0530");
        Assert.Equal(("02E0", "rznb12", "thx_rznb12_service"), (sonya12.UsbProductId, sonya12.ProductId, sonya12.Stack.ServiceName));
        Assert.Equal(("02C6", "rznb11", "thx_rznb11_service"), (sonya11.UsbProductId, sonya11.ProductId, sonya11.Stack.ServiceName));
        Assert.Equal(("02C5", "rznb11", "thx_rznb11_service"), (piper11.UsbProductId, piper11.ProductId, piper11.Stack.ServiceName));
        Assert.Equal(7, sonya12.CodecHardwareIds.Count);
        Assert.False(ThxAudioCompatibility.MatchesCodec(sonya12,
            [@"INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583011"]));
        Assert.False(ThxAudioCompatibility.MatchesCodec(sonya12,
            [@"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&SUBSYS_30101A58"]));
        Assert.False(ThxAudioCompatibility.MatchesCodec(sonya11, piper11.CodecHardwareIds));
        Assert.False(ThxAudioCompatibility.MatchesCodec(piper11, sonya11.CodecHardwareIds));
        foreach (ThxAudioCompatibilityProfile profile in ThxAudioCompatibility.Profiles.Where(profile => profile.Backend == ThxAudioNativeBackend.V4))
        {
            Assert.True(ThxAudioCompatibility.MatchesCodec(profile, profile.CodecHardwareIds));
            Assert.Equal(32, Convert.FromHexString(profile.ActivePropertiesSha256).Length);
            Assert.Equal(32, Convert.FromHexString(profile.SurroundConfigSha256).Length);
            Assert.True(profile.ReadAllowed);
            Assert.True(profile.MutationAllowed);
            Assert.Equal(12, profile.Stack.Packages.Sum(package => package.Files.Count));
            Assert.Contains(profile.Stack.Packages.SelectMany(package => package.Files), file => file.Name.EndsWith("_props.dat", StringComparison.Ordinal));
            Assert.Contains(profile.Stack.Packages.SelectMany(package => package.Files), file => file.Name.EndsWith("_apo4_data.bin", StringComparison.Ordinal));
            Assert.All(profile.Stack.Packages, package =>
            {
                Assert.Contains(package.Files, file => file.Name == package.InfName);
                Assert.Contains(package.Files, file => file.Name == Path.ChangeExtension(package.InfName, ".cat"));
                Assert.All(package.Files, file => Assert.Equal(32, Convert.FromHexString(file.Sha256).Length));
            });
        }
        Assert.Equal("2C5AB38CEB9A5FDD3962FCA12FC7E256DC2BA1A6C138D8DA2933CB74109E0D98",
            sonya11.Stack.Packages.SelectMany(package => package.Files).Single(file => file.Name == "thx_rznb11_props.dat").Sha256);
    }

    [Fact]
    public void OnlyExactRznb12ProfileAdmitsBothWholeRuntimeBuildsAndTargetsCachedBuild()
    {
        ThxAudioCompatibilityProfile sonya12 = ThxAudioCompatibility.Profiles.Single(
            profile => profile.ModelNumber == "RZ09-0581");
        ThxAudioDriverStack[] runtimeStacks = ThxAudioCompatibility.GetRuntimeStacks(sonya12).ToArray();

        Assert.Equal(2, runtimeStacks.Length);
        Assert.Same(sonya12.Stack, runtimeStacks[0]);
        Assert.Same(ThxAudioCompatibility.Rznb12CachedStack, runtimeStacks[1]);
        Assert.Same(ThxAudioCompatibility.Rznb12CachedStack,
            ThxAudioCompatibility.GetProvisioningStack(sonya12));
        Assert.Equal("20349EFE0BAD4ADE84BF57EFC38A7D7B7DF82CCEF3555D17DC1B34142840F6DF",
            runtimeStacks[1].Packages.SelectMany(package => package.Files)
                .Single(file => file.Name == runtimeStacks[1].ServiceExecutable).Sha256);

        foreach (ThxAudioCompatibilityProfile profile in ThxAudioCompatibility.Profiles.Where(
            profile => !ReferenceEquals(profile, sonya12)))
        {
            Assert.Same(profile.Stack, Assert.Single(ThxAudioCompatibility.GetRuntimeStacks(profile)));
            Assert.Same(profile.Stack, ThxAudioCompatibility.GetProvisioningStack(profile));
        }
    }

    [Fact]
    public void RuntimeResolutionDoesNotCombinePackagesFromDifferentBuilds()
    {
        ThxAudioDriverStack first = VariantStack("first build");
        ThxAudioDriverStack second = VariantStack("second build");
        WritePackage("reviewed_apo.inf_amd64_first", first.Packages[0], "first build");
        WritePackage("reviewed_ext.inf_amd64_second", second.Packages[1], "second build");
        WritePackage("reviewed_svc.inf_amd64_second", second.Packages[2], "second build");

        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, first, out _));
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, second, out _));
    }

    [Fact]
    public void CompleteStackWithMissingServiceIsRejected()
    {
        ThxAudioDriverStack stack = VariantStack("complete build");
        WriteWholeStack(stack, "complete build");
        int serviceChecks = 0;

        Assert.False(ThxAudioCompatibility.TryResolveRunningDriverStack(directory, ProfileFor(stack),
            (_, _) => { serviceChecks++; return false; }, out ThxAudioDriverStack? matched,
            out string? binary, out string? error));
        Assert.Equal(1, serviceChecks);
        Assert.Null(matched);
        Assert.Null(binary);
        Assert.Equal(WindowsThxAudioAdmissionSource.GetServiceFailure(stack), error);
    }

    [Fact]
    public void CompleteStackWithMismatchedServiceBinaryIsRejected()
    {
        ThxAudioDriverStack stack = VariantStack("complete build");
        WriteWholeStack(stack, "complete build");
        string observedBinary = Path.Combine(directory, "unreviewed", stack.ServiceExecutable);

        Assert.False(ThxAudioCompatibility.TryResolveRunningDriverStack(directory, ProfileFor(stack),
            (_, expectedBinary) => string.Equals(observedBinary, expectedBinary,
                StringComparison.OrdinalIgnoreCase), out ThxAudioDriverStack? matched,
            out string? binary, out string? error));
        Assert.Null(matched);
        Assert.Null(binary);
        Assert.Equal(WindowsThxAudioAdmissionSource.GetServiceFailure(stack), error);
    }

    [Theory]
    [InlineData("rznb12", "RZ09-0581", true)]
    [InlineData("rznb11", "RZ09-0528", true)]
    [InlineData("rznb11", "RZ09-0530", true)]
    [InlineData("rznb12", "RZ09-0528", false)]
    [InlineData("rznb11", "RZ09-0581", false)]
    [InlineData(null, "RZ09-0581", false)]
    [InlineData("thx-2.6.5", "RZ09-0482", true)]
    [InlineData("thx-2.6.5", "RZ09-0483", true)]
    [InlineData("thx-2.6.5", "RZ09-0484", true)]
    [InlineData("thx-2.6.5", "RZ09-0328", false)]
    [InlineData("rznb11", "RZ09-0482", false)]
    public void NativeAvailabilityRequiresKnownProductModelPair(string? productId, string modelNumber, bool expected)
    {
        var admission = new ThxAudioAdmission(ThxAudioAvailability.Available, null)
        { ProductId = productId, ModelNumber = modelNumber };
        Assert.Equal(expected, admission.IsAvailable);
        Assert.False((admission with { Error = "Admission failed" }).IsAvailable);
    }

    [Fact]
    public void HardwareIdentityRequiresBothExactRegistryValuesAndWholeCodecId()
    {
        var profile = new ThxAudioCompatibilityProfile("RZ09-0581", "Blade 16 - RZ09-0581", "02E0", "rznb12",
            [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A581234"], Stack(), true, true);
        Assert.True(ThxAudioCompatibility.MatchesHost(profile, "Razer", profile.ProductName, "Razer", profile.ProductName));
        Assert.False(ThxAudioCompatibility.MatchesHost(profile, "Razer", profile.ProductName, "Razer", "Blade 16 - RZ09-0528"));
        Assert.False(ThxAudioCompatibility.MatchesHost(profile, "Razer", profile.ProductName, "Other", profile.ProductName));
        Assert.True(ThxAudioCompatibility.MatchesUsb(profile, @"USB\VID_1532&PID_02E0&MI_00\instance"));
        Assert.False(ThxAudioCompatibility.MatchesUsb(profile, @"USB\VID_1532&PID_02E01\instance"));
        Assert.False(ThxAudioCompatibility.MatchesUsb(profile, @"USB\VID_1532&PID_02C6\instance"));
        Assert.True(ThxAudioCompatibility.MatchesCodec(profile, profile.CodecHardwareIds));
        Assert.False(ThxAudioCompatibility.MatchesCodec(profile, [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_9999&SUBSYS_1A581234"]));
        Assert.False(ThxAudioCompatibility.MatchesCodec(profile, [profile.CodecHardwareIds[0] + "&REV_1"]));
    }

    [Fact]
    public void DriverStoreSuffixMayChangeButEveryPackageFileMustMatchTogether()
    {
        ThxAudioDriverStack stack = Stack();
        string package = WritePackage("reviewed_svc.inf_amd64_newinstallation", stack.Packages[0]);
        Assert.True(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out string? binary));
        Assert.Equal(Path.Combine(package, "reviewed_svc.exe"), binary);
        File.WriteAllText(Path.Combine(package, "reviewed_svc.exe"), "changed");
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _, out string? error));
        Assert.Equal("THX driver package reviewed_svc.inf has incompatible or unreadable files: reviewed_svc.exe.", error);
    }

    [Fact]
    public void SplitOrDuplicateMatchingPackagesAreRejected()
    {
        ThxAudioDriverStack stack = Stack();
        string first = WritePackage("reviewed_svc.inf_amd64_first", stack.Packages[0]);
        string second = WritePackage("reviewed_svc.inf_amd64_second", stack.Packages[0]);
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _, out string? duplicateError));
        Assert.Equal("THX driver package reviewed_svc.inf has multiple complete matching copies.", duplicateError);
        File.WriteAllText(Path.Combine(first, "reviewed_svc.exe"), "changed");
        File.WriteAllText(Path.Combine(second, "reviewed_svc.inf"), "changed");
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _, out string? splitError));
        Assert.Equal("THX driver package reviewed_svc.inf has no complete matching copy; affected files: reviewed_svc.inf, reviewed_svc.exe.", splitError);
    }

    [Fact]
    public void MissingPackageAndFileIdentifyReviewedBasenames()
    {
        ThxAudioDriverStack stack = Stack();
        Directory.CreateDirectory(directory);
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _, out string? packageError));
        Assert.Equal("THX driver package reviewed_svc.inf is missing.", packageError);

        WritePackage("reviewed_svc.inf_amd64_incomplete", stack.Packages[0]);
        File.Delete(Path.Combine(directory, "reviewed_svc.inf_amd64_incomplete", "reviewed_svc.cat"));
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _, out string? fileError));
        Assert.Equal("THX driver package reviewed_svc.inf has missing files: reviewed_svc.cat.", fileError);
    }

    [Fact]
    public void PackageSearchIsBoundedAndDoesNotUseUnrelatedFolders()
    {
        ThxAudioDriverStack stack = Stack();
        WritePackage("other_svc.inf_amd64_samefiles", stack.Packages[0]);
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _));
        WritePackage("reviewed_svc.inf_amd64_matching", stack.Packages[0]);
        for (int index = 0; index < 64; index++)
        {
            Directory.CreateDirectory(Path.Combine(directory, $"reviewed_svc.inf_amd64_extra{index}"));
        }
        Assert.False(ThxAudioCompatibility.TryResolveDriverStack(directory, stack, out _));
    }

    [Theory]
    [InlineData("RZ09-0581")]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-0530")]
    public void ActiveConfigurationPinsDefinitionsForEveryProfileButIgnoresMutableUserSettings(string modelNumber)
    {
        ThxAudioCompatibilityProfile profile = WriteActiveConfiguration(modelNumber);
        Assert.True(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory, profile, out string? error));
        Assert.Null(error);
        string productId = profile.ProductId;
        string productDirectory = Path.Combine(directory, "thx", productId);
        File.WriteAllText(Path.Combine(productDirectory, "properties.json"), "changed EQ gains and enhancement levels");
        File.WriteAllText(Path.Combine(productDirectory, "preset_selection"), "changed selected preset");
        Directory.CreateDirectory(Path.Combine(productDirectory, "speakers"));
        File.WriteAllText(Path.Combine(productDirectory, "speakers", "properties.json"), "changed endpoint state");
        Assert.True(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory, profile));
        Assert.False(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory,
            profile with { ProductId = "../rznb12" }));
        Assert.False(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory,
            profile with { ActivePropertiesSha256 = string.Empty }));
    }

    [Theory]
    [InlineData("thx_rznb11_props.dat")]
    [InlineData("surround_714_config.json")]
    public void MissingOrChangedActiveDefinitionBlocksAdmission(string fileName)
    {
        ThxAudioCompatibilityProfile profile = WriteActiveConfiguration("RZ09-0528");
        string file = Path.Combine(directory, "thx", "rznb11", fileName);
        File.WriteAllText(file, "unreviewed updater output");
        Assert.False(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory, profile, out string? changedError));
        Assert.Equal($"The pinned THX active configuration file {fileName} is incompatible or unreadable.", changedError);
        File.Delete(file);
        Assert.False(ThxAudioCompatibility.HasPinnedActiveConfiguration(directory, profile, out string? missingError));
        Assert.Equal($"The pinned THX active configuration file {fileName} is missing.", missingError);
    }

    [Fact]
    public void NativeComponentFailureIdentifiesOnlyItsImmutableBasename()
    {
        string native = Path.Combine(directory, WindowsThxAudioAdmissionSource.NativeDllName);
        Assert.False(WindowsThxAudioAdmissionSource.HasPinnedNativeDll(native, out string? missingError));
        Assert.Equal("The pinned THX native component ThxV4Native_v1.1.1.0.dll is missing.", missingError);

        Directory.CreateDirectory(directory);
        File.WriteAllText(native, "unexpected native component");
        Assert.False(WindowsThxAudioAdmissionSource.HasPinnedNativeDll(native, out string? changedError));
        Assert.Equal("The pinned THX native component ThxV4Native_v1.1.1.0.dll is incompatible or unreadable.", changedError);
        Assert.DoesNotContain(directory, changedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceFailureNamesOnlyPinnedServiceTokensForEveryProfile()
    {
        foreach (ThxAudioCompatibilityProfile profile in ThxAudioCompatibility.Profiles)
        {
            string error = WindowsThxAudioAdmissionSource.GetServiceFailure(profile.Stack);
            Assert.Equal($"THX service {profile.Stack.ServiceName} is missing, stopped, or does not match "
                + $"{profile.Stack.ServiceExecutable}.", error);
            Assert.Equal($"THX service {profile.Stack.ServiceName} could not be verified against "
                + $"{profile.Stack.ServiceExecutable}.",
                WindowsThxAudioAdmissionSource.GetServiceVerificationFailure(profile.Stack));
            Assert.DoesNotContain('\\', error);
            Assert.DoesNotContain(':', error);
        }
    }

    private ThxAudioCompatibilityProfile WriteActiveConfiguration(string modelNumber)
    {
        ThxAudioCompatibilityProfile profile = ThxAudioCompatibility.Profiles.Single(
            profile => profile.ModelNumber == modelNumber) with
        {
            ActivePropertiesSha256 = Convert.ToHexString(SHA256.HashData("reviewed properties archive"u8)),
            SurroundConfigSha256 = Convert.ToHexString(SHA256.HashData("reviewed surround configuration"u8)),
        };
        string productId = profile.ProductId;
        string productDirectory = Path.Combine(directory, "thx", productId);
        Directory.CreateDirectory(productDirectory);
        File.WriteAllBytes(Path.Combine(productDirectory, $"thx_{productId}_props.dat"), "reviewed properties archive"u8.ToArray());
        File.WriteAllBytes(Path.Combine(productDirectory, "surround_714_config.json"), "reviewed surround configuration"u8.ToArray());
        return profile;
    }

    private static ThxAudioDriverStack Stack()
    {
        string hash = Convert.ToHexString(SHA256.HashData("fixture"u8));
        return new("reviewed_service", "reviewed_svc.exe",
            [new("reviewed_svc.inf", [new("reviewed_svc.inf", hash), new("reviewed_svc.exe", hash), new("reviewed_svc.cat", hash)])]);
    }

    private static ThxAudioDriverStack VariantStack(string contents)
    {
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));
        ThxAudioPinnedFile File(string name) => new(name, hash);
        return new("reviewed_service", "reviewed_svc.exe",
        [
            new("reviewed_apo.inf",
                [File("reviewed_apo.inf"), File("reviewed_apo.dll"), File("reviewed_apo.cat")]),
            new("reviewed_ext.inf", [File("reviewed_ext.inf"), File("reviewed_ext.cat")]),
            new("reviewed_svc.inf",
                [File("reviewed_svc.inf"), File("reviewed_svc.exe"), File("reviewed_svc.cat")]),
        ]);
    }

    private static ThxAudioCompatibilityProfile ProfileFor(ThxAudioDriverStack stack) =>
        new("RZ09-TEST", "Reviewed test host", "FFFF", "reviewed", ["REVIEWED\\CODEC"],
            stack, true, true);

    private void WriteWholeStack(ThxAudioDriverStack stack, string contents)
    {
        foreach (ThxAudioDriverPackage package in stack.Packages)
        {
            WritePackage(package.InfName + "_amd64_complete", package, contents);
        }
    }

    private string WritePackage(string name, ThxAudioDriverPackage package, string contents = "fixture")
    {
        string path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        foreach (ThxAudioPinnedFile file in package.Files)
        {
            File.WriteAllText(Path.Combine(path, file.Name), contents);
        }
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

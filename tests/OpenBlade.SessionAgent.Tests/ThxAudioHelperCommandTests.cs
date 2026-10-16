using System.Runtime.InteropServices;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioHelperCommandTests
{
    [Theory]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-0530")]
    public async Task Rznb11ManualEqWritesAndConfirmsAllTenBands(string model)
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        using var output = new StringWriter();
        double[] gains = Enumerable.Range(1, 10).Select(value => (double)value).ToArray();
        string input = System.Text.Json.JsonSerializer.Serialize<ThxAudioHelperRequest>(
            new ThxHelperSelectPresetRequest(1, ThxAudioEndpoint.Speakers, "music", gains),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)
            { ProductId = "rznb11", ModelNumber = model, MutationAllowed = true }),
            new StringReader(input), output);
        await command.RunAsync(CancellationToken.None);
        Assert.Equal(gains, native.Gains);
        Assert.Equal(10, native.PropertyWrites.Count);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal((int)ThxAudioOperationStatus.Succeeded, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(10, json.RootElement.GetProperty("state").GetProperty("equalizerBands").GetArrayLength());
    }

    [Theory]
    [InlineData("RZ09-0581", "rznb12", 2, 0, 0, 3, 4, 3, 1)]
    [InlineData("RZ09-0528", "rznb11", 1, -1, -1, 2, 3, 2, 0)]
    [InlineData("RZ09-0530", "rznb11", 2, -3, -1, 2, 3, 2, 1)]
    public async Task ModelFactoryCurvesUseExactVendorSpeakerSeeds(string model, string product, params int[] expected)
    {
        var native = new FakeNative();
        using var output = new StringWriter();
        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)
            { ProductId = product, ModelNumber = model, MutationAllowed = true }),
            new StringReader("""{"operation":"read","version":1,"endpoint":0}"""), output);
        await command.RunAsync(CancellationToken.None);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(output.ToString());
        System.Text.Json.JsonElement curve = json.RootElement.GetProperty("state").GetProperty("factoryPresetCurves")
            .EnumerateArray().Single(value => value.GetProperty("presetId").GetString() == "music");
        Assert.Equal((model == "RZ09-0581" ? expected : new[] { 0, 0, 0 }.Concat(expected)).Select(value => (double)value),
            curve.GetProperty("gainsDb").EnumerateArray().Select(value => value.GetDouble()));
        Assert.Contains(product, native.LastDevice);
    }

    [Fact]
    public async Task ReadOnlyStackUsesAdmittedProductAndNeverExportsAnotherModelsFactoryCurves()
    {
        var native = new FakeNative();
        using var output = new StringWriter();
        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.AvailableReadOnly, null)
            { ProductId = "rznb11", ModelNumber = "RZ09-0528", MutationAllowed = false }),
            new StringReader("""{"operation":"read","version":1,"endpoint":0}"""), output);
        await command.RunAsync(CancellationToken.None);
        Assert.Contains("rznb11", native.LastDevice);
        Assert.DoesNotContain("rznb12", native.LastDevice);
        Assert.Contains("\"availability\":5", output.ToString());
        Assert.Contains("\"factoryPresetCurves\":[]", output.ToString());
        Assert.Empty(native.PropertyWrites);
        Assert.Equal(0, native.SelectPresetCalls);
    }

    [Theory]
    [InlineData("rznb11", false)]
    [InlineData(null, true)]
    [InlineData("unknown", true)]
    public async Task UnvalidatedOrUnknownStackCannotLoadNativeForMutation(string? product, bool mutationAllowed)
    {
        bool created = false;
        using var output = new StringWriter();
        var command = new ThxAudioHelperCommand(() => { created = true; return new FakeNative(); },
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.AvailableReadOnly, null)
            { ProductId = product, ModelNumber = "RZ09-0528", MutationAllowed = mutationAllowed }),
            new StringReader("""{"operation":"setProperty","version":1,"endpoint":0,"propertyId":"spatial_enabled","value":true}"""), output);
        await command.RunAsync(CancellationToken.None);
        Assert.False(created);
    }

    [Theory]
    [InlineData(218)]
    [InlineData(256)]
    public void VendorJson_AcceptsObservedCatalogAndBoundedHeadroom(int count)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(0, count).Select(index => $"preset-{index}"));

        Assert.Equal(count, ThxVendorJson.ParsePresetIds(json).Count);
    }

    [Fact]
    public void VendorJson_RejectsCatalogBeyondBound()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(0, 257).Select(index => $"preset-{index}"));

        Assert.Throws<InvalidDataException>(() => ThxVendorJson.ParsePresetIds(json));
    }

    [Fact]
    public void VendorJson_ParsesSourceBackedPrimitiveShapes()
    {
        Assert.True(ThxVendorJson.ParseBoolean("true"));
        Assert.False(ThxVendorJson.ParseBoolean("false"));
        Assert.Equal(
            ["music", "game"],
            ThxVendorJson.ParsePresetIds("[\"music\",\"game\"]"));
        Assert.Equal(0.55, ThxVendorJson.ParseFloat("0.55", 0, 1));
        Assert.Equal(-12, ThxVendorJson.ParseFloat("-12", -12, 12));
        Assert.Equal("music", ThxVendorJson.ParseSelectedPreset("\"music\""));
        Assert.Equal("music", ThxVendorJson.ParseSelectedPreset("music"));
        Assert.Equal("ApexLegends_imm", ThxVendorJson.ParseSelectedPreset("ApexLegends_imm"));
        Assert.Null(ThxVendorJson.ParseSelectedPreset("null"));
    }

    [Fact]
    public void VendorJson_RejectsMalformedOrUnboundedShapes()
    {
        Assert.Throws<InvalidDataException>(() => ThxVendorJson.ParseBoolean("1"));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParsePresetIds("[\"music\",\"music\"]"));
        Assert.Throws<InvalidDataException>(() => ThxVendorJson.ParseFloat("NaN", 0, 1));
        Assert.Throws<InvalidDataException>(() => ThxVendorJson.ParseFloat("1.01", 0, 1));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParseSelectedPreset("{\"presetId\":\"music\"}"));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParseSelectedPreset(" music "));
        Assert.Throws<InvalidDataException>(
            () => ThxVendorJson.ParseSelectedPreset(new string('x', 257)));
    }

    [Fact]
    public async Task MutationsNeverInvokeOptionalWindowsEffectsProbe()
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        bool probed = false;
        using var output = new StringWriter();
        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)
            { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true }),
            new StringReader("""{"operation":"setProperty","version":1,"endpoint":0,"propertyId":"spatial_enabled","value":true}"""), output,
            (_, _) => { probed = true; throw new IOException("Unavailable diagnostic"); });
        await command.RunAsync(CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal((int)ThxAudioOperationStatus.Succeeded, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.GetProperty("state").GetProperty("spatialEnabled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            json.RootElement.GetProperty("state").GetProperty("systemEffectsDisabled").ValueKind);
        Assert.Single(native.PropertyWrites);
        Assert.False(probed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadReportsWindowsBypassWithoutChangingConfirmedSpatialSetting(bool probeThrows)
    {
        var native = new FakeNative { Spatial = true };
        using var output = new StringWriter();
        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)
            { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true }),
            new StringReader("""{"operation":"read","version":1,"endpoint":0}"""), output,
            (product, endpoint) =>
            {
                Assert.Equal("rznb12", product);
                Assert.Equal(ThxAudioEndpoint.Speakers, endpoint);
                if (probeThrows)
                {
                    throw new IOException("Unavailable advisory");
                }
                return true;
            });
        await command.RunAsync(CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        var state = json.RootElement.GetProperty("state");
        Assert.True(state.GetProperty("spatialEnabled").GetBoolean());
        Assert.Equal(probeThrows ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.True,
            state.GetProperty("systemEffectsDisabled").ValueKind);
        Assert.Empty(native.PropertyWrites);
    }

    [Fact]
    public async Task Read_DoesNotSetPropertiesAndAlwaysRemovesDevice()
    {
        var native = new FakeNative();
        string input = """
            {"operation":"read","version":1,"endpoint":0}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(native.PropertyWrites);
        Assert.Equal(1, native.AddCalls);
        Assert.Equal(1, native.RemoveCalls);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutation_ReturnsIndeterminateWhenReadbackDoesNotMatch()
    {
        var native = new FakeNative { Spatial = false };
        string input = """
            {"operation":"setProperty","version":1,"endpoint":0,"propertyId":"spatial_enabled","value":true}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Single(native.PropertyWrites);
        Assert.Equal("spatial_enabled", native.PropertyWrites[0].PropertyId);
        Assert.Equal("true", native.PropertyWrites[0].JsonValue);
        Assert.Null(native.PropertyWrites[0].PropertyType);
        Assert.Contains("\"status\":3", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, native.RemoveCalls);
    }

    [Fact]
    public async Task CommandCore_DeniedAdmissionDoesNotConstructNativeAdapter()
    {
        bool constructed = false;
        var admission = new FixedAdmission(new ThxAudioAdmission(
            ThxAudioAvailability.UnsupportedModel,
            "Unsupported test host."));
        string input = """
            {"operation":"read","version":1,"endpoint":0}
            """;
        var output = new StringWriter();
        var command = new ThxAudioHelperCommand(
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            admission,
            new StringReader(input),
            output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(constructed);
        Assert.Contains("\"status\":1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandCore_RejectsOversizedInputBeforeAdmissionOrNativeConstruction()
    {
        int admissionChecks = 0;
        bool constructed = false;
        string input = new('x', ThxAudioHelperProtocol.MaximumLineCharacters + 1);
        var output = new StringWriter();
        var command = new ThxAudioHelperCommand(
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            new CountingAdmission(() => admissionChecks++),
            new StringReader(input),
            output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, admissionChecks);
        Assert.False(constructed);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelperHost_UntrustedParentCannotConstructNativeAdapter()
    {
        bool constructed = false;
        var channel = new FakeClientChannel(
            "{\"operation\":\"read\",\"version\":1,\"endpoint\":0}");
        var host = new ThxAudioHelperHost(
            new FakeConnector(channel),
            new FixedParentVerifier(trusted: false),
            () =>
            {
                constructed = true;
                return new FakeNative();
            },
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null) { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true }));
        int parentProcessId = 42;
        var invocation = new ThxAudioHelperInvocation(
            ThxAudioHelperProtocol.CreatePipeName(parentProcessId, Guid.NewGuid()),
            parentProcessId);

        int exitCode = await host.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.False(constructed);
        Assert.Equal(string.Empty, channel.OutputText);
    }

    [Fact]
    public void HelperInvocation_RequiresFixedPidBoundNonceShape()
    {
        int parentProcessId = 42;
        string pipeName = ThxAudioHelperProtocol.CreatePipeName(
            parentProcessId,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));

        Assert.True(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, pipeName, "42"],
            out ThxAudioHelperInvocation? invocation));
        Assert.Equal(parentProcessId, invocation!.ParentProcessId);
        Assert.False(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, pipeName, "43"],
            out _));
        Assert.False(ThxAudioHelperInvocation.TryParse(
            [ThxAudioHelperProtocol.CommandSwitch, $"{pipeName}x", "42"],
            out _));
    }

    [Fact]
    public async Task ProcessClient_AssignmentFailureSendsNoRequest()
    {
        var child = new FakeChild
        {
            AssignResult = false,
            TerminateResult = true,
            HasExitedValue = true,
        };
        var factory = new FakeChildFactory(child);
        await using var client = new ThxAudioHelperProcessClient(childFactory: factory);

        ThxAudioWorkerResult result = await client.ExecuteAsync(
            new ThxHelperReadRequest(ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Failed, result.Status);
        Assert.Equal(0, child.ExchangeCalls);
        Assert.Equal(1, child.TerminateCalls);
    }

    [Fact]
    public async Task ProcessClient_UnconfirmedExitBlocksSubsequentOperations()
    {
        var child = new FakeChild
        {
            AssignResult = true,
            ConfirmExitResult = false,
            TerminateResult = false,
            HasExitedValue = false,
            Response = SuccessfulHelperResponse(spatial: true),
        };
        var factory = new FakeChildFactory(child);
        await using var client = new ThxAudioHelperProcessClient(childFactory: factory);

        ThxAudioWorkerResult uncertain = await client.ExecuteAsync(
            new ThxHelperSetPropertyRequest(
                ThxAudioHelperProtocol.Version,
                ThxAudioEndpoint.Speakers,
                "spatial_enabled",
                true),
            CancellationToken.None);
        ThxAudioWorkerResult blocked = await client.ExecuteAsync(
            new ThxHelperReadRequest(ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Indeterminate, uncertain.Status);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, blocked.Status);
        Assert.Equal(1, factory.StartCalls);
        Assert.Equal(1, child.ExchangeCalls);
    }

    [Fact]
    public async Task SelectPreset_RejectsIdMissingFromFreshVendorListBeforeMutation()
    {
        var native = new FakeNative
        {
            PresetListJson = "[\"music\",\"game\"]",
        };
        string input = """
            {"operation":"selectPreset","version":1,"endpoint":0,"presetId":"movie","gainsDb":[0,0,0,0,0,0,0]}
            """;
        var output = new StringWriter();
        var command = Command(native, input, output);

        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, native.PresetListCalls);
        Assert.Equal(0, native.SelectPresetCalls);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, native.RemoveCalls);
    }

    [Fact]
    public async Task SelectVoice_MapsToNativePodcastAndReturnsLogicalVoice()
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        string input = """
            {"operation":"selectPreset","version":1,"endpoint":1,"presetId":"voice","gainsDb":[0,0,0,0,0,0,0,0,0,0]}
            """;
        var output = new StringWriter();

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("podcast", native.SelectedPreset);
        Assert.Equal(10, native.PropertyWrites.Count);
        Assert.All(native.PropertyWrites, write => Assert.Equal("Float", write.PropertyType));
        Assert.Contains("\"selectedPresetId\":\"voice\"", output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("\"selectedNativeProfileId\":\"podcast\"", output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_PreservesUnknownGameProfileAsReadOnlyNativeId()
    {
        var native = new FakeNative
        {
            SelectedPreset = "ApexLegends_imm",
            PresetListJson =
                "[\"default\",\"custom\",\"music\",\"movie\",\"game\",\"podcast\",\"ApexLegends_imm\"]",
        };
        var output = new StringWriter();

        int exitCode = await Command(
            native,
            "{\"operation\":\"read\",\"version\":1,\"endpoint\":1}",
            output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"selectedPresetId\":null", output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("\"selectedNativeProfileId\":\"ApexLegends_imm\"", output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"ApexLegends_imm\"", output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectSpeakerPreset_AppliesCompleteCurveWithThreePinnedBands()
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        var output = new StringWriter();
        string input = """
            {"operation":"selectPreset","version":1,"endpoint":0,"presetId":"custom","gainsDb":[-3,-2,-1,0,1,2,3]}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("custom", native.SelectedPreset);
        Assert.Equal(10, native.PropertyWrites.Count);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(index => $"eq_band_{index}_gain_db"),
            native.PropertyWrites.Select(write => write.PropertyId));
        Assert.All(native.PropertyWrites, write => Assert.Equal("Float", write.PropertyType));
        Assert.Equal([0d, 0d, 0d, -3d, -2d, -1d, 0d, 1d, 2d, 3d], native.Gains);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPreset_PartialCurveWriteIsIndeterminate()
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            FailWriteAt = 5,
        };
        var output = new StringWriter();
        string input = """
            {"operation":"selectPreset","version":1,"endpoint":1,"presetId":"custom","gainsDb":[0,1,2,3,4,5,6,7,8,9]}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, native.PropertyWrites.Count);
        Assert.Contains("\"status\":3", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPreset_SkipsNativeWritesWhenSelectionAndCurveMatch()
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        var output = new StringWriter();

        int exitCode = await Command(
            native,
            "{\"operation\":\"selectPreset\",\"version\":1,\"endpoint\":0,\"presetId\":\"music\",\"gainsDb\":[0,0,0,0,0,0,0]}",
            output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, native.SelectPresetCalls);
        Assert.Empty(native.PropertyWrites);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RZ09-0581", "rznb12")]
    [InlineData("RZ09-0528", "rznb11")]
    [InlineData("RZ09-0530", "rznb11")]
    public async Task ApplicationProfile_ResolvesPinnedMappingAndEnablesSpatialFirst(string model, string product)
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            PresetListJson =
                "[\"default\",\"custom\",\"music\",\"movie\",\"game\",\"podcast\",\"ApexLegends_cmm\"]",
        };
        var output = new StringWriter();
        string input = """
            {"operation":"applyApplicationProfile","version":1,"endpoint":1,"executableName":"r5apex.exe","mode":1}
            """;

        var command = new ThxAudioHelperCommand(() => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null)
            { ProductId = product, ModelNumber = model, MutationAllowed = true }),
            new StringReader(input), output);
        int exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Collection(
            native.PropertyWrites,
            write =>
            {
                Assert.Equal("spatial_enabled", write.PropertyId);
                Assert.Equal("true", write.JsonValue);
                Assert.Null(write.PropertyType);
            });
        Assert.Equal("ApexLegends_cmm", native.SelectedPreset);
        Assert.Equal(1, native.SelectPresetCalls);
        Assert.Contains(product, native.LastDevice);
        Assert.Contains("\"applicationProfiles\":0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationProfile_RejectsUnsupportedModeBeforeMutation()
    {
        var native = new FakeNative { ApplyWritesToReadback = true };
        var output = new StringWriter();
        string input = """
            {"operation":"applyApplicationProfile","version":1,"endpoint":1,"executableName":"shootergame.exe","mode":1}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(native.PropertyWrites);
        Assert.Equal(0, native.SelectPresetCalls);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationProfileCatalog_IsExactBoundedPinnedTable()
    {
        Assert.Equal(231, ThxAudioApplicationProfileCatalog.Profiles.Count);
        Assert.True(ThxAudioApplicationProfileCatalog.TryResolve(
            "r5apex.exe",
            ThxAudioApplicationMode.Immersive,
            out string immersive));
        Assert.Equal("ApexLegends_imm", immersive);
        Assert.False(ThxAudioApplicationProfileCatalog.TryResolve(
            "shootergame.exe",
            ThxAudioApplicationMode.Competitive,
            out _));

        IReadOnlyList<ThxAudioApplicationProfile> installed =
            ThxAudioApplicationProfileCatalog.AvailableProfiles(
                ["ApexLegends_imm", "ARKSurvivalEvolved_imm"]);
        ThxAudioApplicationProfile apex = Assert.Single(
            installed,
            profile => profile.ExecutableName == "r5apex.exe");
        Assert.True(apex.SupportsImmersive);
        Assert.False(apex.SupportsCompetitive);
        Assert.DoesNotContain(
            installed,
            profile => profile.ExecutableName == "cs2.exe");
        Assert.Equal(
            installed.Select(profile => profile.ExecutableName)
                .OrderBy(name => name, StringComparer.Ordinal),
            installed.Select(profile => profile.ExecutableName));
    }

    [Fact]
    public async Task SetNormalization_WritesChangedBooleanThenNormalizedFloat()
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            NormalizationEnabled = false,
            NormalizationLevel = 0.55,
        };
        var output = new StringWriter();
        string input = """
            {"operation":"setEnhancement","version":1,"endpoint":0,"kind":0,"enabled":true,"levelPercent":60}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Collection(
            native.PropertyWrites,
            write =>
            {
                Assert.Equal("volume_leveling_enabled", write.PropertyId);
                Assert.Equal("true", write.JsonValue);
                Assert.Null(write.PropertyType);
            },
            write =>
            {
                Assert.Equal("volume_leveling_level", write.PropertyId);
                Assert.Equal("0.6", write.JsonValue);
                Assert.Equal("Float", write.PropertyType);
            });
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetVoiceClarity_SkipsPropertiesAlreadyAtRequestedState()
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            VoiceClarityEnabled = true,
            VoiceClarityLevel = 0.6,
        };
        var output = new StringWriter();
        string input = """
            {"operation":"setEnhancement","version":1,"endpoint":1,"kind":1,"enabled":true,"levelPercent":60}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(native.PropertyWrites);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnhancementToggle_PreservesFractionalNativeLevel()
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            NormalizationEnabled = false,
            NormalizationLevel = 0.123456,
        };
        var output = new StringWriter();
        string input = """
            {"operation":"setEnhancement","version":1,"endpoint":0,"kind":0,"enabled":true,"levelPercent":null}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Collection(
            native.PropertyWrites,
            write =>
            {
                Assert.Equal("volume_leveling_enabled", write.PropertyId);
                Assert.Equal("true", write.JsonValue);
                Assert.Null(write.PropertyType);
            });
        Assert.Equal(0.123456, native.NormalizationLevel);
        Assert.Contains("\"levelPercent\":12", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnhancementDisable_PreservesFractionalNativeLevel()
    {
        var native = new FakeNative
        {
            ApplyWritesToReadback = true,
            VoiceClarityEnabled = true,
            VoiceClarityLevel = 0.123456,
        };
        var output = new StringWriter();
        string input = """
            {"operation":"setEnhancement","version":1,"endpoint":1,"kind":1,"enabled":false,"levelPercent":null}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Collection(
            native.PropertyWrites,
            write =>
            {
                Assert.Equal("voice_clarity_enabled", write.PropertyId);
                Assert.Equal("false", write.JsonValue);
                Assert.Null(write.PropertyType);
            });
        Assert.Equal(0.123456, native.VoiceClarityLevel);
        Assert.Contains("\"levelPercent\":12", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\":0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnhancementDisable_RejectsNonNullLevelBeforeNativeRegistration()
    {
        var native = new FakeNative();
        var output = new StringWriter();
        string input = """
            {"operation":"setEnhancement","version":1,"endpoint":1,"kind":1,"enabled":false,"levelPercent":50}
            """;

        int exitCode = await Command(native, input, output).RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, native.AddCalls);
        Assert.Empty(native.PropertyWrites);
        Assert.Contains("\"status\":2", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("USB\\VID_1532&PID_02E0", true)]
    [InlineData("USB\\VID_1532&PID_02E0\\ABC", true)]
    [InlineData("USB\\VID_1532&PID_02E0&MI_00\\ABC", true)]
    [InlineData("USB\\VID_1532&PID_02E00\\ABC", false)]
    [InlineData("USB\\VID_1532&PID_02E0X\\ABC", false)]
    public void UsbAdmission_RequiresExactPidBoundary(string pnpDeviceId, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsThxAudioAdmissionSource.IsExactUsbPnpDeviceId(pnpDeviceId));
    }

    [Fact]
    public void NativeDelegates_AreCdeclAndBooleanReturnsAreOneByte()
    {
        Type[] delegates = typeof(ThxAudioNativeAdapter).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic);
        Type[] nativeDelegates = delegates.Where(type =>
            type.BaseType == typeof(MulticastDelegate)).ToArray();

        Assert.NotEmpty(nativeDelegates);
        foreach (Type type in nativeDelegates)
        {
            var convention = type.GetCustomAttributes(
                typeof(UnmanagedFunctionPointerAttribute),
                inherit: false).Cast<UnmanagedFunctionPointerAttribute>().Single();
            Assert.Equal(CallingConvention.Cdecl, convention.CallingConvention);
            if (type.GetMethod("Invoke")!.ReturnType == typeof(bool))
            {
                var marshal = type.GetMethod("Invoke")!.ReturnParameter
                    .GetCustomAttributes(typeof(MarshalAsAttribute), inherit: false)
                    .Cast<MarshalAsAttribute>()
                    .Single();
                Assert.Equal(UnmanagedType.I1, marshal.Value);
            }
        }
    }

    private sealed class FakeNative : IThxAudioNativeAdapter
    {
        public bool Spatial { get; set; }
        public bool Equalizer { get; set; }
        public bool ApplyWritesToReadback { get; set; }
        public int? FailWriteAt { get; set; }
        public bool NormalizationEnabled { get; set; }
        public double NormalizationLevel { get; set; } = 0.55;
        public bool VoiceClarityEnabled { get; set; }
        public double VoiceClarityLevel { get; set; } = 0.55;
        public double[] Gains { get; } = new double[10];
        public string SelectedPreset { get; set; } = "music";
        public string PresetListJson { get; set; } =
            "[\"default\",\"custom\",\"music\",\"movie\",\"game\",\"podcast\"]";
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int PresetListCalls { get; private set; }
        public int SelectPresetCalls { get; private set; }
        public List<(string PropertyId, string JsonValue, string? PropertyType)> PropertyWrites
        {
            get;
        } = [];

        public string? LastDevice { get; private set; }

        public bool AddDevice(string deviceJson)
        {
            AddCalls++;
            LastDevice = deviceJson;
            return true;
        }

        public bool CheckStatus(string deviceJson) => true;

        public bool RemoveDevice(string deviceJson)
        {
            RemoveCalls++;
            return true;
        }

        public string GetProperty(string deviceJson, string propertyId) => propertyId switch
        {
            "spatial_enabled" => Spatial ? "true" : "false",
            "eq_enabled" => Equalizer ? "true" : "false",
            "volume_leveling_enabled" => NormalizationEnabled ? "true" : "false",
            "volume_leveling_level" => Json(NormalizationLevel),
            "voice_clarity_enabled" => VoiceClarityEnabled ? "true" : "false",
            "voice_clarity_level" => Json(VoiceClarityLevel),
            _ when propertyId.StartsWith("eq_band_", StringComparison.Ordinal)
                && propertyId.EndsWith("_gain_db", StringComparison.Ordinal)
                && int.TryParse(
                    propertyId["eq_band_".Length..^"_gain_db".Length],
                    out int index)
                && index is >= 1 and <= 10 => Json(Gains[index - 1]),
            _ => throw new InvalidOperationException(),
        };

        public bool SetProperty(
            string deviceJson,
            string propertyId,
            string jsonValue,
            string? propertyType)
        {
            PropertyWrites.Add((propertyId, jsonValue, propertyType));
            if (PropertyWrites.Count == FailWriteAt)
            {
                return false;
            }
            if (ApplyWritesToReadback)
            {
                switch (propertyId)
                {
                    case "spatial_enabled":
                        Spatial = bool.Parse(jsonValue);
                        break;
                    case "eq_enabled":
                        Equalizer = bool.Parse(jsonValue);
                        break;
                    case "volume_leveling_enabled":
                        NormalizationEnabled = bool.Parse(jsonValue);
                        break;
                    case "volume_leveling_level":
                        NormalizationLevel = double.Parse(
                            jsonValue,
                            System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "voice_clarity_enabled":
                        VoiceClarityEnabled = bool.Parse(jsonValue);
                        break;
                    case "voice_clarity_level":
                        VoiceClarityLevel = double.Parse(
                            jsonValue,
                            System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        int index = int.Parse(
                            propertyId["eq_band_".Length..^"_gain_db".Length],
                            System.Globalization.CultureInfo.InvariantCulture);
                        Gains[index - 1] = double.Parse(
                            jsonValue,
                            System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
            return true;
        }

        public string GetPresetList(string deviceJson, string presetGroupId)
        {
            PresetListCalls++;
            return PresetListJson;
        }

        public string GetSelectedPreset(string deviceJson, string presetGroupId) =>
            SelectedPreset;

        public bool SelectPreset(string deviceJson, string presetGroupId, string presetId)
        {
            SelectPresetCalls++;
            if (ApplyWritesToReadback)
            {
                SelectedPreset = presetId;
            }
            return true;
        }

        public void Dispose()
        {
        }

        private static string Json(double value) =>
            System.Text.Json.JsonSerializer.Serialize(value);
    }

    private static ThxAudioHelperCommand Command(
        FakeNative native,
        string input,
        StringWriter output) => new(
            () => native,
            new FixedAdmission(new ThxAudioAdmission(ThxAudioAvailability.Available, null) { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true }),
            new StringReader(input),
            output);

    private sealed class FixedAdmission(ThxAudioAdmission value) : IThxAudioAdmissionSource
    {
        public ThxAudioAdmission Check() => value;
    }

    private sealed class CountingAdmission(Action observed) : IThxAudioAdmissionSource
    {
        public ThxAudioAdmission Check()
        {
            observed();
            return new ThxAudioAdmission(ThxAudioAvailability.Available, null) { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true };
        }
    }

    private sealed class FakeClientChannel(string request) : IThxAudioHelperClientChannel
    {
        private readonly StringWriter output = new();

        public TextReader Input { get; } = new StringReader(request);

        public TextWriter Output => output;

        public string OutputText => output.ToString();

        public ValueTask DisposeAsync()
        {
            Input.Dispose();
            output.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnector(IThxAudioHelperClientChannel channel)
        : IThxAudioHelperChannelConnector
    {
        public Task<IThxAudioHelperClientChannel> ConnectAsync(
            ThxAudioHelperInvocation invocation,
            CancellationToken cancellationToken) => Task.FromResult(channel);
    }

    private sealed class FixedParentVerifier(bool trusted) : IThxAudioHelperParentVerifier
    {
        public bool IsTrustedParent(
            IThxAudioHelperClientChannel channel,
            int expectedParentProcessId) => trusted;
    }

    private sealed class FakeChildFactory(IThxAudioHelperChild child)
        : IThxAudioHelperChildFactory
    {
        public int StartCalls { get; private set; }

        public IThxAudioHelperChild Start()
        {
            StartCalls++;
            return child;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeChild : IThxAudioHelperChild
    {
        public bool AssignResult { get; init; }
        public bool ConfirmExitResult { get; init; } = true;
        public bool TerminateResult { get; init; } = true;
        public bool HasExitedValue { get; init; } = true;
        public int ExchangeCalls { get; private set; }
        public int TerminateCalls { get; private set; }
        public ThxAudioHelperResponse Response { get; init; } = SuccessfulHelperResponse();

        public bool HasExited => HasExitedValue;

        public int? ExitCode => HasExitedValue ? 0 : null;

        public bool AssignToLifetimeOwner() => AssignResult;

        public Task<ThxAudioHelperResponse> ExchangeAsync(
            ThxAudioHelperRequest request,
            CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            return Task.FromResult(Response);
        }

        public Task<bool> ConfirmExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ConfirmExitResult);

        public Task<bool> TerminateAndConfirmExitAsync()
        {
            TerminateCalls++;
            return Task.FromResult(TerminateResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ThxAudioHelperResponse SuccessfulHelperResponse(bool spatial = false) => new(
        ThxAudioHelperProtocol.Version,
        ThxAudioOperationStatus.Succeeded,
        ThxAudioAvailability.Available,
        null,
        new ThxAudioState(
            ThxAudioEndpoint.Speakers,
            spatial,
            EqualizerEnabled: false,
            SelectedPresetId: "music",
            Presets: [new ThxAudioPreset("music", "music")]));
}

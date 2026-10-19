using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class LegacyThxAudioCommandTests
{
    private const string Output = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
    private static ThxAudioCompatibilityProfile Profile(string model = "RZ09-0483") =>
        ThxAudioCompatibility.Profiles.Single(value => value.ModelNumber == model);
    private static ThxAudioAdmission Available(string model = "RZ09-0483") =>
        new(ThxAudioAvailability.Available, null)
        { ProductId = "thx-2.6.5", ModelNumber = model, MutationAllowed = true };

    [Fact]
    public void EverySupportedModelHasAnExplicitThxOrNonThxDecision()
    {
        var admitted = ThxAudioCompatibility.Profiles.Select(profile => (profile.ModelNumber, profile.UsbProductId));
        var excluded = ThxAudioCompatibility.ExcludedModels.Select(profile => (profile.ModelNumber, profile.UsbProductId));
        Assert.Equal(RazerBladeModelCatalog.All.Select(model => (model.ModelNumber, model.UsbProductId))
            .OrderBy(model => model.ModelNumber), admitted.Concat(excluded).OrderBy(model => model.ModelNumber));
        Assert.DoesNotContain(ThxAudioCompatibility.Profiles, profile => profile.ModelNumber == "RZ09-0328");
    }

    [Theory]
    [InlineData("RZ09-0482")]
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0484")]
    public void LegacyModelsUseExactSeparateCodecIdentities(string model)
    {
        ThxAudioCompatibilityProfile profile = Profile(model);
        Assert.Equal(ThxAudioNativeBackend.Legacy265, profile.Backend);
        foreach (var other in ThxAudioCompatibility.Profiles.Where(value => value.ModelNumber != model))
        {
            Assert.False(ThxAudioCompatibility.MatchesCodec(profile, other.CodecHardwareIds));
        }
        Assert.Equal("VSSrv", profile.Stack.ServiceName);
    }

    [Theory]
    [InlineData("RZ09-0482")]
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0484")]
    public async Task LegacyReadRoutesAwayFromV4AndReleasesComBeforeTheNextAwait(string model)
    {
        var fake = new Native(new LegacyThxAudioNativeState(Output, true, false));
        string json = JsonSerializer.Serialize<ThxAudioHelperRequest>(new ThxHelperReadRequest(
            ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var writer = new StringWriter();
        int exit = await new ThxAudioHelperCommand(
            () => throw new InvalidOperationException("V4 must not load"), new Admission(Available(model)),
            new StringReader(json + "\n"), writer, createLegacyNative: () => fake, readLegacyOutput: (_, _) => Output)
            .RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(fake.Disposed);
        Assert.Equal(0, fake.Writes);
        using JsonDocument response = JsonDocument.Parse(writer.ToString());
        Assert.Null(response.RootElement.GetProperty("error").GetString());
        Assert.True(response.RootElement.GetProperty("state").GetProperty("spatialEnabled").GetBoolean());
        Assert.Equal((int)ThxAudioFeatureAccess.ObservedOnly,
            response.RootElement.GetProperty("state").GetProperty("equalizer").GetInt32());
    }

    [Fact]
    public void BufferedFirstRequestDispatchesLegacyComOffTheStaEntryThread()
    {
        Exception? failure = null;
        bool activatedInMta = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var writer = new StringWriter();
                string json = JsonSerializer.Serialize<ThxAudioHelperRequest>(new ThxHelperReadRequest(
                    ThxAudioHelperProtocol.Version, ThxAudioEndpoint.Speakers), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                new ThxAudioHelperCommand(() => throw new InvalidOperationException("V4 must not load"),
                    new Admission(Available()), new StringReader(json + "\n"), writer,
                    createLegacyNative: () =>
                    {
                        activatedInMta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
                        return new Native(new LegacyThxAudioNativeState(Output, true, false));
                    }, readLegacyOutput: (_, _) => Output).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.True(activatedInMta);
    }

    [Fact]
    public void SpatialMutationRequiresFreshAdmissionAndIndependentReadback()
    {
        var fake = new Native(new LegacyThxAudioNativeState(Output, false, false), new LegacyThxAudioNativeState(Output, false, false), new LegacyThxAudioNativeState(Output, true, false));
        var checks = new Admission(Available());
        var command = new LegacyThxAudioCommand(() => fake, checks, (_, _) => Output);
        var result = command.Execute(SetSpatial(), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Succeeded, result.Status);
        Assert.True(result.State!.SpatialEnabled);
        Assert.Equal(1, checks.Calls);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(3, fake.Reads);
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputChangesBeforeSubmissionBlockTheWrite(bool wrongListeningMode)
    {
        var fake = new Native(new LegacyThxAudioNativeState(wrongListeningMode ? Output : "another-output", false, wrongListeningMode));
        var command = new LegacyThxAudioCommand(() => fake, new Admission(Available()), (_, _) => Output);
        var result = command.Execute(SetSpatial(), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Failed, result.Status);
        Assert.Contains("Select the output currently used by Windows", result.Error);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public void AdmissionLossBeforeSubmissionDoesNotWrite()
    {
        var fake = new Native(new LegacyThxAudioNativeState(Output, false, false));
        var command = new LegacyThxAudioCommand(() => fake,
            new Admission(new(ThxAudioAvailability.ControllerActive, "Controller active")), (_, _) => Output);
        var result = command.Execute(SetSpatial(), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Unavailable, result.Status);
        Assert.Equal(0, fake.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfirmedWriteStopsWithoutRetry(bool endpointChanged)
    {
        var fake = new Native(new LegacyThxAudioNativeState(Output, false, false), new LegacyThxAudioNativeState(Output, false, false),
            new LegacyThxAudioNativeState(endpointChanged ? "changed-output" : Output, false, false));
        var command = new LegacyThxAudioCommand(() => fake, new Admission(Available()), (_, _) => Output);
        var result = command.Execute(SetSpatial(), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, result.Status);
        Assert.Null(result.State);
        Assert.Equal(1, fake.Writes);
    }

    [Fact]
    public void NativeFailureAfterSubmissionIsIndeterminate()
    {
        var fake = new Native(new LegacyThxAudioNativeState(Output, false, false)) { ThrowAfterWrite = true };
        var command = new LegacyThxAudioCommand(() => fake, new Admission(Available()), (_, _) => Output);
        var result = command.Execute(SetSpatial(), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, result.Status);
        Assert.Equal(1, fake.Writes);
    }

    [Fact]
    public void UnreviewedLegacyControlsDoNotConstructNativeOrMutate()
    {
        var command = new LegacyThxAudioCommand(() => throw new InvalidOperationException("Unexpected activation"),
            new Admission(Available()), (_, _) => Output);
        var result = command.Execute(new ThxHelperSetPropertyRequest(ThxAudioHelperProtocol.Version,
            ThxAudioEndpoint.Speakers, "eq_enabled", true), Available(), Profile());
        Assert.Equal(ThxAudioOperationStatus.Failed, result.Status);
    }

    private static ThxHelperSetPropertyRequest SetSpatial() => new(ThxAudioHelperProtocol.Version,
        ThxAudioEndpoint.Speakers, "spatial_enabled", true);

    private sealed class Admission(ThxAudioAdmission value) : IThxAudioAdmissionSource
    {
        internal int Calls;
        public ThxAudioAdmission Check() { Calls++; return value; }
    }

    private sealed class Native(params LegacyThxAudioNativeState[] states) : ILegacyThxAudioNativeAdapter
    {
        internal bool Disposed;
        internal int Writes;
        internal int Reads;
        internal bool ThrowAfterWrite;
        public LegacyThxAudioNativeState Read() => states[Math.Min(Reads++, states.Length - 1)];
        public void SetSpatial(bool value)
        {
            Writes++;
            if (ThrowAfterWrite)
            {
                throw new IOException("Possible submission");
            }
        }
        public void Dispose() => Disposed = true;
    }
}

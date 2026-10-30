using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryMainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.TelemetryViewModel.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DisabledByDefault_DoesNotDemandOrCaptureUntilEnabled()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = false;
        viewModel.SetTelemetryPresentationActive(true);
        client.Observation = Status(2, withReadings: true);

        Assert.False(viewModel.TelemetryEnabled);
        Assert.False(viewModel.RequiresTelemetryObservation);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        Assert.Empty(client.Requests);
        Assert.Equal(0, viewModel.TelemetryHistorySnapshot.RawSampleCount);

        await viewModel.SetTelemetryEnabledAsync(true);
        Assert.True(viewModel.RequiresTelemetryObservation);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        Assert.Equal([LiveReadingSensors.All], client.Requests);
        Assert.Equal(1, viewModel.TelemetryHistorySnapshot.RawSampleCount);
    }

    [Fact]
    public async Task Disabled_RejectsRecordingAndReplayBeforeOpeningTheirSources()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        bool recordingFactoryCalled = false;
        viewModel.SetTelemetryRecordingFactoryForTests((_, _, _, _) =>
        {
            recordingFactoryCalled = true;
            return Task.FromResult<ITelemetryRecordingSession>(new GatedRecordingSession());
        });

        InvalidOperationException recordingException =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.StartTelemetryRecordingAsync("disabled.csv", CancellationToken.None));
        InvalidOperationException replayException =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.OpenTelemetryReplayAsync("missing.csv", CancellationToken.None));

        Assert.Equal(TrayText.Get("Telemetry is disabled."), recordingException.Message);
        Assert.Equal(TrayText.Get("Telemetry is disabled."), replayException.Message);
        Assert.False(recordingFactoryCalled);
        Assert.False(viewModel.IsTelemetryRecording);
        Assert.Null(viewModel.TelemetryReplaySession);
    }

    [Fact]
    public async Task Disposed_RejectsTelemetryEnableRecordingAndReplay()
    {
        var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            viewModel.SetTelemetryEnabledAsync(true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            viewModel.StartTelemetryRecordingAsync("disposed.csv", CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            viewModel.OpenTelemetryReplayAsync("disposed.csv", CancellationToken.None));
    }

    [Fact]
    public async Task DisposeDuringReplayRead_DoesNotPublishReplay()
    {
        var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRead = new TaskCompletionSource<TelemetrySessionDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SetTelemetryReplayReaderForTests(async (_, _, _) =>
        {
            readStarted.TrySetResult();
            return await allowRead.Task;
        });

        Task replay = viewModel.OpenTelemetryReplayAsync("replay.csv", CancellationToken.None);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.DisposeAsync();
        allowRead.TrySetResult(new TelemetrySessionDocument(
            TelemetrySessionMetadata.CreateLive(Start),
            [],
            []));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => replay);
        Assert.Null(viewModel.TelemetryReplaySession);
    }

    [Fact]
    public async Task DisposeDuringRecordingStart_DoesNotPublishRecording()
    {
        var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        var recording = new GatedRecordingSession();
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SetTelemetryRecordingFactoryForTests(async (_, _, _, _) =>
        {
            factoryStarted.TrySetResult();
            await allowFactory.Task;
            return recording;
        });

        Task start = viewModel.StartTelemetryRecordingAsync(
            "disposed.csv",
            CancellationToken.None);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task dispose = viewModel.DisposeAsync().AsTask();
        allowFactory.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(recording.DisposeCalled.Task.IsCompletedSuccessfully);
        Assert.False(viewModel.IsTelemetryRecording);
    }

    [Fact]
    public async Task Disable_DrainsAcceptedAppendAndFinalizesActiveRecording()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = false;
        var recording = new GatedRecordingSession();
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("accepted.csv", CancellationToken.None);
        client.Observation = Status(2, withReadings: true);
        Task observation = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        await recording.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disable = viewModel.SetTelemetryEnabledAsync(false);
        await WaitForAsync(() => !viewModel.IsTelemetryRecording);

        Assert.False(viewModel.TelemetryEnabled);
        Assert.False(viewModel.RequiresTelemetryObservation);
        Assert.False(recording.StopCalled.Task.IsCompleted);
        recording.AllowAppend.TrySetResult();
        await observation;
        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(recording.StopCalled.Task.IsCompletedSuccessfully);
        Assert.Single(recording.AppendedSamples);
    }

    [Fact]
    public async Task Disable_QueuesOffPreferenceBeforeBlockedRecordingFinalization()
    {
        string preferencePath = Path.Combine(directory, "preferences.json");
        var preferenceStore = new UiPreferenceStore(preferencePath);
        await preferenceStore.SaveAsync(new UiPreferences(
            ThemePreference.FollowWindows,
            TelemetryEnabled: true));
        Exception? saveFailure = null;
        var preferenceSaveQueue = new UiPreferenceSaveQueue(
            preferenceStore.SaveAsync,
            exception => saveFailure = exception);
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(
            client,
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        bool telemetryToggleInProgress = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (telemetryToggleInProgress
                && args.PropertyName == nameof(MainViewModel.TelemetryEnabled))
            {
                preferenceSaveQueue.Enqueue(new UiPreferences(
                    ThemePreference.FollowWindows,
                    TelemetryEnabled: viewModel.TelemetryEnabled));
            }
        };
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        var recording = new GatedRecordingSession(gateStop: true);
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("blocked.csv", CancellationToken.None);
        client.Observation = Status(2, withReadings: true);
        Task observation = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        await recording.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        telemetryToggleInProgress = true;
        Task disable = viewModel.SetTelemetryEnabledAsync(false);
        await WaitForAsync(() => !viewModel.TelemetryEnabled);
        await preferenceSaveQueue.DisposeAsync();

        Assert.False((await preferenceStore.LoadAsync()).TelemetryEnabled);
        Assert.Null(saveFailure);
        Assert.False(disable.IsCompleted);
        Assert.False(recording.StopCalled.Task.IsCompleted);

        recording.AllowAppend.TrySetResult();
        await observation;
        await recording.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disable.IsCompleted);
        recording.AllowStop.TrySetResult();
        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        telemetryToggleInProgress = false;
    }

    [Fact]
    public async Task Disable_StopFailureLeavesFeatureOffAndPartialPathRecoverable()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        var recording = new GatedRecordingSession(
            partialPath: "failed.csv.partial",
            stopFailure: new IOException("Disk full."));
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("failed.csv", CancellationToken.None);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            viewModel.SetTelemetryEnabledAsync(false));

        Assert.Equal("Disk full.", exception.Message);
        Assert.False(viewModel.TelemetryEnabled);
        Assert.False(viewModel.IsTelemetryRecording);
        Assert.Contains("failed.csv.partial", viewModel.TelemetryErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_RacingRecordingStartWaitsAndFinalizesCreatedSession()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        var recording = new GatedRecordingSession(destinationPath: "raced.csv");
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SetTelemetryRecordingFactoryForTests(async (_, _, _, _) =>
        {
            factoryStarted.TrySetResult();
            await allowFactory.Task;
            return recording;
        });

        Task start = viewModel.StartTelemetryRecordingAsync(
            "raced.csv",
            CancellationToken.None);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disable = viewModel.SetTelemetryEnabledAsync(false);
        Assert.False(viewModel.TelemetryEnabled);
        Assert.False(disable.IsCompleted);

        allowFactory.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await disable.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(recording.StopCalled.Task.IsCompletedSuccessfully);
        Assert.False(viewModel.IsTelemetryRecording);
        Assert.False(viewModel.TelemetryEnabled);
    }

    [Fact]
    public async Task ActivePresentation_RequestsAllSensorsAndCapturesEachRevisionOnce()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = false;
        viewModel.SetTelemetryPresentationActive(true);

        ServiceStatusSnapshot observed = Status(2, withReadings: true, withApplication: true);
        client.Observation = observed;
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);

        Assert.Equal([LiveReadingSensors.All, LiveReadingSensors.All], client.Requests);
        Assert.Equal(2, viewModel.TelemetryHistorySnapshot.RawSampleCount);
        TelemetrySample sample = Assert.IsType<TelemetrySample>(viewModel.LatestTelemetrySample);
        Assert.Equal(Start.AddSeconds(1), sample.TimestampUtc);
        Assert.Equal(2, sample.ServiceRevision);
        Assert.Contains(observed.ActiveApplicationProfileId!.Value.ToString("D"), sample.ProfileIdentity);
        Assert.Contains("Game", sample.ProfileIdentity);
        Assert.Equal(TelemetryFreshness.Fresh, viewModel.LatestTelemetryFreshness);
    }

    [Fact]
    public async Task UnverifiedObservation_CapturesMissingReadingsInsteadOfStaleValues()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = false;
        viewModel.SetTelemetryPresentationActive(true);
        client.Observation = Status(2, withReadings: true) with
        {
            DeviceVerified = false,
        };

        await viewModel.ObserveTrayStateAsync(CancellationToken.None);

        TelemetrySample sample = Assert.IsType<TelemetrySample>(viewModel.LatestTelemetrySample);
        Assert.Null(sample.BatteryPowerFlowMilliwatts);
        Assert.Null(sample.Fan1Rpm);
        Assert.Null(sample.Fan2Rpm);
        Assert.Null(sample.CpuTemperatureCelsius);
        Assert.Null(sample.GpuTemperatureCelsius);
        Assert.Equal(1, viewModel.TelemetryHistorySnapshot.RawSampleCount);
    }

    [Fact]
    public async Task Replay_PreservesRecordedFirstSampleMarkersAndKeepsEmptyReplaySeparateFromLive()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "markers.csv");
        var metadata = TelemetrySessionMetadata.CreateLive(Start) with { EndedAtUtc = Start.AddSeconds(10) };
        var sample = new TelemetrySample(Start, 1, TelemetrySource.LiveService, PowerSource.Battery,
            "Battery", null, null, null, null, null);
        var marker = new TelemetryEvent(Start, TelemetryEventKind.PowerSourceChanged, "PluggedIn", "Battery");
        await TelemetryCsv.WriteAsync(path, new(metadata, [sample], [marker]));
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.SetTelemetryPresentationActive(true);
        client.Observation = Status(2, withReadings: true);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        Assert.NotNull(viewModel.LatestTelemetrySample);

        await viewModel.OpenTelemetryReplayAsync(path, CancellationToken.None);
        Assert.Equal(marker, Assert.Single(viewModel.GetTelemetrySnapshot().Events));
        string empty = Path.Combine(directory, "empty.csv");
        await TelemetryCsv.WriteAsync(empty, new(metadata, [], []));
        await viewModel.OpenTelemetryReplayAsync(empty, CancellationToken.None);
        Assert.Equal(TelemetrySource.LocalCsvReplay, viewModel.DisplayedTelemetrySource);
        Assert.Null(viewModel.LatestTelemetrySample);
        Assert.Null(viewModel.LatestTelemetryFreshness);
        Assert.Equal(0, viewModel.GetTelemetrySnapshot().RawSampleCount);
        await viewModel.SetTelemetryEnabledAsync(false);
        Assert.Null(viewModel.TelemetryReplaySession);
        Assert.False(viewModel.TelemetryEnabled);
        Assert.NotNull(viewModel.LatestTelemetrySample);
    }

    [Fact]
    public async Task Recording_KeepsDemandActiveWhilePresentationIsHiddenAndFinalizesReplayableCsv()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "recording.csv");
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = false;

        Assert.Null(viewModel.DisplayedTelemetrySourceSessionId);
        await viewModel.StartTelemetryRecordingAsync(path, CancellationToken.None);
        Assert.NotNull(viewModel.DisplayedTelemetrySessionMetadata);
        Assert.Null(viewModel.DisplayedTelemetrySourceSessionId);
        Assert.True(viewModel.IsTelemetryRecording);
        Assert.True(viewModel.RequiresTelemetryObservation);
        client.Observation = Status(2, withReadings: true);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        Assert.Equal(path, await viewModel.StopTelemetryRecordingAsync(CancellationToken.None));

        Assert.False(viewModel.IsTelemetryRecording);
        Assert.Null(viewModel.DisplayedTelemetrySourceSessionId);
        Assert.False(viewModel.RequiresTelemetryObservation);
        Assert.Equal([LiveReadingSensors.All], client.Requests);
        TelemetrySessionDocument replay = await TelemetryCsv.ReadAsync(path);
        Assert.Single(replay.Samples);
        Assert.Equal(61, replay.Samples[0].CpuTemperatureCelsius);

        await viewModel.OpenTelemetryReplayAsync(path, CancellationToken.None);
        Assert.Equal(TelemetrySource.LocalCsvReplay, viewModel.DisplayedTelemetrySource);
        Assert.Equal(TelemetryFreshness.Replay, viewModel.LatestTelemetryFreshness);
        Assert.Equal(replay.Metadata.SessionId, viewModel.DisplayedTelemetrySourceSessionId);
        Assert.Equal(1, viewModel.TelemetryHistorySnapshot.RawSampleCount);
        viewModel.CloseTelemetryReplay();
        Assert.Null(viewModel.DisplayedTelemetrySourceSessionId);
        Assert.Equal(1, viewModel.TelemetryHistorySnapshot.RawSampleCount);
    }

    [Fact]
    public async Task Stop_WaitsForAnAcceptedAppendBeforeFinalizing()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        var recording = new GatedRecordingSession();
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("accepted.csv", CancellationToken.None);
        client.Observation = Status(2, withReadings: true);

        Task observation = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        await recording.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string?> stop = viewModel.StopTelemetryRecordingAsync(CancellationToken.None);

        await WaitForAsync(() => !viewModel.IsTelemetryRecording);
        Assert.False(recording.StopCalled.Task.IsCompleted);
        recording.AllowAppend.TrySetResult();
        await observation;
        Assert.Equal("accepted.csv", await stop);
        Assert.Single(recording.AppendedSamples);
        Assert.Equal(61, recording.AppendedSamples[0].CpuTemperatureCelsius);
    }

    [Fact]
    public async Task StopFailure_ExposesPartialFileAndAllowsANewRecording()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        var failed = new GatedRecordingSession(
            partialPath: "failed.csv.partial",
            stopFailure: new IOException("Disk full."));
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(failed));
        await viewModel.StartTelemetryRecordingAsync("failed.csv", CancellationToken.None);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => viewModel.StopTelemetryRecordingAsync(CancellationToken.None));

        Assert.Equal("Disk full.", exception.Message);
        Assert.False(viewModel.IsTelemetryRecording);
        Assert.Null(viewModel.TelemetryRecordingPath);
        Assert.Contains("failed.csv.partial", viewModel.TelemetryErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Disk full.", viewModel.TelemetryErrorMessage, StringComparison.Ordinal);

        var replacement = new GatedRecordingSession(destinationPath: "replacement.csv");
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(replacement));
        await viewModel.StartTelemetryRecordingAsync("replacement.csv", CancellationToken.None);

        Assert.True(viewModel.IsTelemetryRecording);
        Assert.Equal("replacement.csv", await viewModel.StopTelemetryRecordingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopDuringBackpressure_WriterFaultDrainsAppendAndAllowsRestart()
    {
        string path = Path.Combine(directory, "backpressure.csv");
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var fixture = await FailingTelemetryRecordingFixture.CreateAsync(
            path, TelemetrySessionMetadata.CreateLive(Start), time);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(fixture.Recording));
        await viewModel.StartTelemetryRecordingAsync(path, CancellationToken.None);
        client.Observation = Status(2, withReadings: true);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        await fixture.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        client.Observation = Status(3, withReadings: true);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        client.Observation = Status(4, withReadings: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task observation = viewModel.ObserveTrayStateAsync(cancellation.Token);
        Assert.False(observation.IsCompleted);
        Task<string?> stop = viewModel.StopTelemetryRecordingAsync(CancellationToken.None);

        try
        {
            fixture.FailWrite();
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                stop.WaitAsync(TimeSpan.FromSeconds(5)));
            await observation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Injected telemetry disk failure", error.Message, StringComparison.Ordinal);
            Assert.False(viewModel.IsTelemetryRecording);
            Assert.Null(viewModel.TelemetryRecordingPath);
            Assert.Contains(path + ".partial", viewModel.TelemetryErrorMessage, StringComparison.Ordinal);
            Assert.True(File.Exists(path + ".partial"));
            Assert.False(File.Exists(path));

            var replacement = new GatedRecordingSession(destinationPath: "replacement.csv");
            viewModel.SetTelemetryRecordingFactoryForTests(
                (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(replacement));
            await viewModel.StartTelemetryRecordingAsync("replacement.csv", CancellationToken.None);
            Assert.Equal("replacement.csv", await viewModel.StopTelemetryRecordingAsync(CancellationToken.None));
        }
        finally
        {
            cancellation.Cancel();
            fixture.FailWrite();
            try
            {
                await observation;
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                await stop;
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AsynchronousWriterFailure_ClearsRecordingAndAllowsImmediateRestart()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        var failed = new GatedRecordingSession(
            destinationPath: "failed.csv",
            partialPath: "failed.csv.partial");
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(failed));
        await viewModel.StartTelemetryRecordingAsync("failed.csv", CancellationToken.None);

        failed.FailWriter(new IOException("The recording disk is full."));
        await WaitForAsync(() => !viewModel.IsTelemetryRecording
            && viewModel.TelemetryRecordingPath is null);

        Assert.Contains("failed.csv.partial", viewModel.TelemetryErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("disk is full", viewModel.TelemetryErrorMessage,
            StringComparison.OrdinalIgnoreCase);

        var replacement = new GatedRecordingSession(destinationPath: "replacement.csv");
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(replacement));
        await viewModel.StartTelemetryRecordingAsync("replacement.csv", CancellationToken.None);

        Assert.True(viewModel.IsTelemetryRecording);
        Assert.Equal("replacement.csv", await viewModel.StopTelemetryRecordingAsync(
            CancellationToken.None));
        Assert.Equal(string.Empty, viewModel.TelemetryErrorMessage);
    }

    [Fact]
    public async Task AutomaticCapacityCompletion_FinalizesDestinationAndAllowsImmediateRestart()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        var completed = new GatedRecordingSession(destinationPath: "capacity.csv");
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(completed));
        await viewModel.StartTelemetryRecordingAsync("capacity.csv", CancellationToken.None);

        completed.CompleteAutomatically("The telemetry recording sample limit was reached.");
        await WaitForAsync(() => !viewModel.IsTelemetryRecording
            && viewModel.TelemetryRecordingPath is null);

        Assert.Contains("saved to capacity.csv", viewModel.TelemetryErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partial", viewModel.TelemetryErrorMessage,
            StringComparison.OrdinalIgnoreCase);

        var replacement = new GatedRecordingSession(destinationPath: "replacement.csv");
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(replacement));
        await viewModel.StartTelemetryRecordingAsync("replacement.csv", CancellationToken.None);
        Assert.Equal("replacement.csv", await viewModel.StopTelemetryRecordingAsync(
            CancellationToken.None));
    }

    [Fact]
    public async Task WriterFaultDuringDispose_DoesNotEscapeViewModelCleanup()
    {
        var client = new FakeServiceClient();
        var viewModel = new MainViewModel(
            client,
            new ImmediateDispatcher(),
            new MutableTimeProvider(Start));
        await viewModel.SetTelemetryEnabledAsync(true);
        var failed = new GatedRecordingSession(
            disposeFailure: new IOException("Writer failed while shutting down."));
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(failed));
        await viewModel.StartTelemetryRecordingAsync("failed.csv", CancellationToken.None);
        failed.FailWriter(new IOException("Writer failed while shutting down."));

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task CancellationAfterStopAdmission_DoesNotAbandonFinalization()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        var recording = new GatedRecordingSession(gateStop: true);
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("accepted.csv", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        Task<string?> stop = viewModel.StopTelemetryRecordingAsync(cancellation.Token);
        await recording.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(stop.IsCompleted);
        recording.AllowStop.TrySetResult();
        Assert.Equal("accepted.csv", await stop);
        Assert.False(viewModel.IsTelemetryRecording);
        Assert.Null(viewModel.TelemetryRecordingPath);
    }

    [Fact]
    public async Task Replay_SuppressesVisibleLiveDemandButRetainsBatteryWarningReadings()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "replay.csv");
        TelemetrySessionMetadata metadata = TelemetrySessionMetadata.CreateLive(Start);
        await using (TelemetryRecordingSession session = await TelemetryRecordingSession.StartAsync(
            path,
            metadata,
            timeProvider: new MutableTimeProvider(Start.AddSeconds(1))))
        {
            await session.AppendAsync(TelemetrySample.Project(
                Status(1, withReadings: true),
                Start));
        }
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(Start);
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher(), time);
        await viewModel.SetTelemetryEnabledAsync(true);
        await viewModel.StartAsync();
        client.Publish(Status(1));
        await WaitForAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LiveReadingsEnabled = true;
        viewModel.SetTelemetryPresentationActive(true);
        viewModel.SetDashboardTelemetryPresentationActive(false);
        viewModel.SetTrayDashboardTelemetryPresentationActive(false);
        Assert.True(viewModel.RequiresTelemetryObservation);

        await viewModel.OpenTelemetryReplayAsync(path, CancellationToken.None);
        Assert.False(viewModel.RequiresTelemetryObservation);
        client.Requests.Clear();
        client.Observation = Status(1, withReadings: true);
        await viewModel.ObserveStateAsync(CancellationToken.None);
        Assert.Equal([LiveReadingSensors.BatteryPower], client.Requests);
        Assert.NotNull(viewModel.TelemetryReplaySession);
        client.Requests.Clear();

        var recording = new GatedRecordingSession();
        recording.AllowAppend.TrySetResult();
        viewModel.SetTelemetryRecordingFactoryForTests(
            (_, _, _, _) => Task.FromResult<ITelemetryRecordingSession>(recording));
        await viewModel.StartTelemetryRecordingAsync("during-replay.csv", CancellationToken.None);
        Assert.True(viewModel.RequiresTelemetryObservation);
        client.Observation = Status(2, withReadings: true);
        await viewModel.ObserveStateAsync(CancellationToken.None);
        Assert.Equal([LiveReadingSensors.All], client.Requests);
        await viewModel.StopTelemetryRecordingAsync(CancellationToken.None);
        Assert.False(viewModel.RequiresTelemetryObservation);

        viewModel.CloseTelemetryReplay();
        Assert.True(viewModel.RequiresTelemetryObservation);
        client.Observation = Status(3, withReadings: true);
        await viewModel.ObserveStateAsync(CancellationToken.None);
        Assert.Equal([LiveReadingSensors.All, LiveReadingSensors.All], client.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ServiceStatusSnapshot Status(
        long revision,
        bool withReadings = false,
        bool withApplication = false)
    {
        Guid applicationId = Guid.Parse("2a76f0d6-e2ba-4f86-b282-30a578431bca");
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            ApplicationProfiles = withApplication
                ?
                [
                    new ApplicationProfileDefinition(
                        applicationId,
                        "Game",
                        @"C:\Games\Game.exe",
                        ApplicationActivationMode.Foreground,
                        new ApplicationProfileOverrideSet()),
                ]
                : [],
        };
        return new ServiceStatusSnapshot(
            revision,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.SafeTargetDefaults(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: withReadings ? new LiveReadings(12_000, 2_100, 2_200, 61, 57) : null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false)
        {
            ActiveApplicationProfileId = withApplication ? applicationId : null,
        };
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FakeServiceClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates = Channel.CreateUnbounded<ServiceWatchUpdate>();

        public ServiceStatusSnapshot? Observation { get; set; }

        public List<LiveReadingSensors> Requests { get; } = [];

        public void Publish(ServiceStatusSnapshot status) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(status));

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public override Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors requestedLiveReadings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(requestedLiveReadings);
            return Task.FromResult(Observation
                ?? throw new InvalidOperationException("No observation was configured."));
        }
    }

    private sealed class GatedRecordingSession(
        string destinationPath = "accepted.csv",
        string partialPath = "accepted.csv.partial",
        Exception? stopFailure = null,
        bool gateStop = false,
        Exception? disposeFailure = null) : ITelemetryRecordingSession
    {
        private bool active = true;
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => completion.Task;

        public string? AutomaticCompletionReason { get; private set; }

        public TaskCompletionSource AppendStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowAppend { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopCalled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStop { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeCalled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TelemetrySample> AppendedSamples { get; } = [];

        public bool IsActive => active;

        public bool RequiresSampling => active;

        public string DestinationPath => destinationPath;

        public string PartialPath => partialPath;

        public TelemetrySessionMetadata Metadata { get; } =
            TelemetrySessionMetadata.CreateLive(Start);

        public async ValueTask AppendAsync(
            TelemetrySample sample,
            IReadOnlyList<TelemetryEvent>? markers = null,
            CancellationToken cancellationToken = default)
        {
            AppendStarted.TrySetResult();
            await AllowAppend.Task.WaitAsync(cancellationToken);
            AppendedSamples.Add(sample);
        }

        public async Task<string> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            active = false;
            StopCalled.TrySetResult();
            if (gateStop)
            {
                await AllowStop.Task.WaitAsync(cancellationToken);
            }
            if (stopFailure is null)
            {
                completion.TrySetResult();
                return destinationPath;
            }
            throw stopFailure;
        }

        public ValueTask DisposeAsync()
        {
            active = false;
            AllowAppend.TrySetResult();
            DisposeCalled.TrySetResult();
            if (disposeFailure is not null)
            {
                return ValueTask.FromException(disposeFailure);
            }
            return ValueTask.CompletedTask;
        }

        public void FailWriter(Exception exception)
        {
            active = false;
            completion.TrySetException(exception);
        }

        public void CompleteAutomatically(string reason)
        {
            active = false;
            AutomaticCompletionReason = reason;
            completion.TrySetResult();
        }
    }
}

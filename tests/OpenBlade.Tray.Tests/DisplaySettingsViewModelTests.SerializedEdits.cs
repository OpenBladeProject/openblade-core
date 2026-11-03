using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class DisplaySettingsViewModelTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task BrightnessProfileRequiresSupportedLevelsAndReadback(bool nullLevels, bool hasReadback)
    {
        var adapter = new FakeDisplaySettingsAdapter(Snapshot(Fixed60.Key, false, Native.Key) with
        {
            SupportedBrightnessLevels = nullLevels ? null! : [],
            BrightnessPercent = hasReadback ? (byte)50 : null,
        });
        await using var viewModel = CreateViewModel(adapter);
        var profile = new DisplayProfileSettings(null, null, null) { BrightnessPercent = 50 };
        viewModel.ProfilePersistenceAvailable = true;
        viewModel.ActiveProfileSettings = profile;

        bool applied = await viewModel.ApplyProfileAsync(PowerSource.PluggedIn, profile);

        Assert.False(applied);
        Assert.True(viewModel.HasProfileDifference);
        Assert.DoesNotContain("failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshPreservesColorSelectionWhenWindowsReturnsDifferentCasing()
    {
        var adapter = new FakeDisplaySettingsAdapter(Snapshot(Fixed60.Key, false, Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.SetExternalState(Snapshot(Fixed60.Key, false, Native.Key.ToUpperInvariant()));

        await viewModel.RefreshAsync();

        Assert.Equal(Native, viewModel.SelectedColorProfile);
        Assert.Contains(Native.Label, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("outside OpenBlade", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColorProfileConfirmationMatchesSessionDormancyAndIdentity(bool hdrEnabled)
    {
        var adapter = new FakeDisplaySettingsAdapter(Snapshot(Fixed60.Key, hdrEnabled, Native.Key))
        {
            ProfileColorReadback = Srgb.Key,
        };
        await using var viewModel = CreateViewModel(adapter);
        var profile = new DisplayProfileSettings(null, null, "SRGB");
        viewModel.ActiveProfileSettings = profile;

        bool applied = await viewModel.ApplyProfileAsync(PowerSource.PluggedIn, profile);

        Assert.True(applied);
        Assert.False(viewModel.HasProfileDifference);
        Assert.Equal(hdrEnabled, viewModel.IsHdrEnabled);
        Assert.Equal(hdrEnabled ? Native.Key : Srgb.Key, viewModel.SelectedColorProfile?.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshQueuedBehindUserChangeComparesAgainstCompletedEdit(bool subsequentExternalChange)
    {
        var adapter = new FakeDisplaySettingsAdapter(Snapshot(Fixed60.Key, false, Native.Key))
        {
            BlockFirstRefreshChange = true,
        };
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task change = viewModel.SelectRefreshRateAsync(Fixed240, timeout.Token);
        Task? refresh = null;
        try
        {
            await adapter.FirstRefreshChangeStarted.Task.WaitAsync(timeout.Token);
            adapter.BlockNextStateRead = true;
            refresh = viewModel.RefreshAsync(timeout.Token);
            adapter.ReleaseFirstRefreshChange.TrySetResult();
            await adapter.StateReadStarted.Task.WaitAsync(timeout.Token);
            if (subsequentExternalChange)
            {
                adapter.SetExternalState(Snapshot(Dynamic.Key, false, Native.Key));
            }
            adapter.ReleaseStateRead.TrySetResult();
            await Task.WhenAll(change, refresh).WaitAsync(timeout.Token);

            if (subsequentExternalChange)
            {
                Assert.Equal("Refresh rate changed outside OpenBlade.", viewModel.StatusMessage);
            }
            else
            {
                Assert.DoesNotContain("outside OpenBlade", viewModel.StatusMessage, StringComparison.Ordinal);
            }
            Assert.Equal(subsequentExternalChange ? Dynamic.Key : Fixed240.Key, viewModel.SelectedRefreshRate?.Key);
        }
        finally
        {
            timeout.Cancel();
            adapter.ReleaseFirstRefreshChange.TrySetResult();
            adapter.ReleaseStateRead.TrySetResult();
            _ = await Record.ExceptionAsync(() => Task.WhenAll(change, refresh ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData("refresh", true)]
    [InlineData("refresh", false)]
    [InlineData("hdr", true)]
    [InlineData("hdr", false)]
    [InlineData("brightness", true)]
    [InlineData("brightness", false)]
    [InlineData("color", true)]
    [InlineData("color", false)]
    public async Task QueuedEditChecksForNoOpAgainstCompletedRefresh(
        string field,
        bool initiallyMatches)
    {
        DisplaySettingsSnapshot initial = Snapshot(Fixed60.Key, false, Native.Key) with
        {
            BrightnessPercent = 50,
            SupportedBrightnessLevels = [0, 50, 70, 100],
        };
        DisplaySettingsSnapshot refreshed = field switch
        {
            "refresh" => initial with { SelectedRefreshRateKey = Fixed240.Key },
            "hdr" => initial with { IsHdrEnabled = true },
            "brightness" => initial with { BrightnessPercent = 70 },
            "color" => initial with { SelectedColorProfileKey = Srgb.Key },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var adapter = new FakeDisplaySettingsAdapter(initial);
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        var changes = new List<DisplayProfileSettingsChangedEventArgs>();
        viewModel.UserSettingsChanged += (_, args) => changes.Add(args);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        adapter.BlockNextStateRead = true;
        Task refresh = viewModel.RefreshAsync(timeout.Token);
        Task? change = null;
        try
        {
            await adapter.StateReadStarted.Task.WaitAsync(timeout.Token);
            change = field switch
            {
                "refresh" => viewModel.SelectRefreshRateAsync(initiallyMatches ? Fixed60 : Fixed240, timeout.Token),
                "hdr" => viewModel.SetHdrEnabledAsync(!initiallyMatches, timeout.Token),
                "brightness" => viewModel.SetBrightnessAsync(initiallyMatches ? (byte)50 : (byte)70, timeout.Token),
                "color" => viewModel.SelectColorProfileAsync(initiallyMatches ? Native : Srgb, timeout.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            adapter.SetExternalState(refreshed);
            adapter.ReleaseStateRead.TrySetResult();
            await Task.WhenAll(refresh, change).WaitAsync(timeout.Token);

            int requestCount = adapter.RefreshRequests.Count + adapter.HdrRequests.Count
                + adapter.BrightnessRequests.Count + adapter.ColorRequests.Count;
            Assert.Equal(initiallyMatches ? 1 : 0, requestCount);
            Assert.Equal(initiallyMatches ? 1 : 0, changes.Count);
            DisplaySettingsSnapshot expected = initiallyMatches ? initial : refreshed;
            Assert.Equal(expected.SelectedRefreshRateKey, viewModel.SelectedRefreshRate?.Key);
            Assert.Equal(expected.IsHdrEnabled, viewModel.IsHdrEnabled);
            Assert.Equal(expected.BrightnessPercent, viewModel.BrightnessPercent);
            Assert.Equal(expected.SelectedColorProfileKey, viewModel.SelectedColorProfile?.Key);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            timeout.Cancel();
            adapter.ReleaseStateRead.TrySetResult();
            _ = await Record.ExceptionAsync(() => Task.WhenAll(refresh, change ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task QueuedBrightnessUsesLevelsFromCompletedRefresh()
    {
        DisplaySettingsSnapshot initial = Snapshot(Fixed60.Key, false, Native.Key) with
        {
            BrightnessPercent = 50,
            SupportedBrightnessLevels = [0, 50, 70, 100],
        };
        var adapter = new FakeDisplaySettingsAdapter(initial);
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        adapter.BlockNextStateRead = true;
        Task refresh = viewModel.RefreshAsync(timeout.Token);
        Task? change = null;
        try
        {
            await adapter.StateReadStarted.Task.WaitAsync(timeout.Token);
            change = viewModel.SetBrightnessAsync(70, timeout.Token);
            adapter.SetExternalState(initial with { SupportedBrightnessLevels = [0, 50, 75, 100] });
            adapter.ReleaseStateRead.TrySetResult();
            await Task.WhenAll(refresh, change).WaitAsync(timeout.Token);

            Assert.Equal([(byte)75], adapter.BrightnessRequests);
            Assert.Equal((byte)75, viewModel.BrightnessPercent);
        }
        finally
        {
            timeout.Cancel();
            adapter.ReleaseStateRead.TrySetResult();
            _ = await Record.ExceptionAsync(() => Task.WhenAll(refresh, change ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedColorEditUsesHdrStateFromCompletedRefresh(bool initialHdr)
    {
        DisplaySettingsSnapshot initial = Snapshot(Fixed60.Key, initialHdr, Native.Key);
        var adapter = new FakeDisplaySettingsAdapter(initial);
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        adapter.BlockNextStateRead = true;
        Task refresh = viewModel.RefreshAsync(timeout.Token);
        Task? change = null;
        try
        {
            await adapter.StateReadStarted.Task.WaitAsync(timeout.Token);
            change = viewModel.SelectColorProfileAsync(Srgb, timeout.Token);
            adapter.SetExternalState(initial with { IsHdrEnabled = !initialHdr });
            adapter.ReleaseStateRead.TrySetResult();
            await Task.WhenAll(refresh, change).WaitAsync(timeout.Token);

            Assert.Equal(initialHdr ? [Srgb.Key] : Array.Empty<string>(), adapter.ColorRequests);
            Assert.Equal(!initialHdr, viewModel.IsHdrEnabled);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            timeout.Cancel();
            adapter.ReleaseStateRead.TrySetResult();
            _ = await Record.ExceptionAsync(() => Task.WhenAll(refresh, change ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }
}

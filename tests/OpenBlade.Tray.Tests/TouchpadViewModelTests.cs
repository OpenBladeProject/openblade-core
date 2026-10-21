using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TouchpadViewModelTests
{
    [Fact]
    public void RefreshUsesConfirmedStateAndDisablesUnknownState()
    {
        var controller = new Controller { Enabled = false };
        var model = new TouchpadViewModel(controller);
        Assert.False(model.CanEdit);
        model.Refresh();
        Assert.True(model.CanEdit);
        Assert.False(model.IsOn);
        controller.Enabled = true;
        model.Refresh();
        Assert.True(model.IsOn);
        controller.Enabled = null;
        model.Refresh();
        Assert.False(model.CanEdit);
        Assert.True(model.IsOn);
    }

    [Fact]
    public async Task SetReadsFreshStateAndDoesNotInvertAnExternalChange()
    {
        var controller = new Controller { Enabled = true };
        var model = new TouchpadViewModel(controller);
        model.Refresh();
        controller.Enabled = false;
        await model.SetEnabledAsync(false);
        Assert.Equal(0, controller.Toggles);
        Assert.False(model.IsOn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetSubmitsOnceAndConfirmsRequestedState(bool enabled)
    {
        var controller = new Controller { Enabled = !enabled };
        var model = new TouchpadViewModel(controller);
        await model.SetEnabledAsync(enabled);
        Assert.Equal(1, controller.Toggles);
        Assert.Equal(enabled, model.IsOn);
        Assert.True(model.CanEdit);
    }

    [Fact]
    public async Task UnknownFreshReadDoesNotSendInput()
    {
        var controller = new Controller { Enabled = true };
        var model = new TouchpadViewModel(controller);
        model.Refresh();
        controller.Enabled = null;
        await model.SetEnabledAsync(false);
        Assert.Equal(0, controller.Toggles);
        Assert.False(model.CanEdit);
    }

    [Fact]
    public async Task AmbiguousSubmissionIsNotReplayedAndUsesReadback()
    {
        var controller = new Controller { Enabled = true, ThrowAfterToggle = true };
        var model = new TouchpadViewModel(controller);
        await model.SetEnabledAsync(false);
        Assert.Equal(1, controller.Toggles);
        Assert.False(model.IsOn);
        Assert.Contains("could not be confirmed", model.Description);
        Assert.False(model.CanEdit);
        await model.SetEnabledAsync(true);
        Assert.Equal(1, controller.Toggles);
        model.Refresh();
        Assert.True(model.CanEdit);
    }

    [Fact]
    public async Task TimeoutAndConcurrentRequestsNeverReplayToggle()
    {
        var controller = new Controller { Enabled = true, IgnoreToggle = true };
        var model = new TouchpadViewModel(controller);
        Task pending = model.SetEnabledAsync(false);
        Assert.False(model.CanEdit);
        await model.SetEnabledAsync(false);
        model.Refresh();
        await pending;
        Assert.Equal(1, controller.Toggles);
        Assert.True(model.IsOn);
        Assert.Contains("could not be confirmed", model.Description);
        Assert.False(model.CanEdit);
        await model.SetEnabledAsync(true);
        Assert.Equal(1, controller.Toggles);
        model.Refresh();
        Assert.True(model.CanEdit);
    }

    private sealed class Controller : ITouchpadController
    {
        public bool? Enabled { get; set; }
        public bool ThrowAfterToggle { get; init; }
        public bool IgnoreToggle { get; init; }
        public int Toggles { get; private set; }
        public bool? ReadEnabled() => Enabled;
        public void Toggle()
        {
            Toggles++;
            if (!IgnoreToggle)
            {
                Enabled = !Enabled;
            }
            if (ThrowAfterToggle)
            {
                throw new InvalidOperationException();
            }
        }
    }
}

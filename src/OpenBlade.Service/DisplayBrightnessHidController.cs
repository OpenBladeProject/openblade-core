using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IDisplayBrightnessHidController
{
    Task<ControlOperationResult> SetUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken cancellationToken);
}

internal enum DisplayBrightnessHidWriteStatus
{
    Applied,
    Unavailable,
    Failed,
}

internal readonly record struct DisplayBrightnessHidWriteResult(
    DisplayBrightnessHidWriteStatus Status,
    string? Error = null);

internal interface IDisplayBrightnessHidTransport
{
    DisplayBrightnessHidWriteResult SetUsage(DisplayBrightnessHidUsage usage);
}

public sealed class DisplayBrightnessHidController : IDisplayBrightnessHidController
{
    private readonly IDisplayBrightnessHidTransport transport;
    private readonly ILogger<DisplayBrightnessHidController>? logger;

    public DisplayBrightnessHidController()
        : this(new WindowsDisplayBrightnessHidTransport(), null)
    {
    }

    public DisplayBrightnessHidController(ILogger<DisplayBrightnessHidController> logger)
        : this(new WindowsDisplayBrightnessHidTransport(), logger)
    {
    }

    internal DisplayBrightnessHidController(IDisplayBrightnessHidTransport transport)
        : this(transport, null)
    {
    }

    internal DisplayBrightnessHidController(
        IDisplayBrightnessHidTransport transport,
        ILogger<DisplayBrightnessHidController>? logger)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.logger = logger;
    }

    public Task<ControlOperationResult> SetUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(usage))
        {
            throw new ArgumentOutOfRangeException(nameof(usage));
        }

        cancellationToken.ThrowIfCancellationRequested();
        DisplayBrightnessHidWriteResult write = transport.SetUsage(usage);
        cancellationToken.ThrowIfCancellationRequested();
        if (write.Status == DisplayBrightnessHidWriteStatus.Unavailable
            && !string.IsNullOrWhiteSpace(write.Error))
        {
            logger?.LogWarning(
                "Display-brightness HID access is unavailable: {BrightnessHidError}",
                write.Error);
        }

        return Task.FromResult(write.Status switch
        {
            DisplayBrightnessHidWriteStatus.Applied => new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Windows received the display-brightness HID state."),
            DisplayBrightnessHidWriteStatus.Unavailable => new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The OpenBlade display-brightness HID device is unavailable."),
            _ => new ControlOperationResult(
                ControlOperationStatus.Failed,
                string.IsNullOrWhiteSpace(write.Error)
                    ? "The display-brightness HID state could not be confirmed."
                    : $"The display-brightness HID state could not be confirmed: {write.Error}"),
        });
    }
}

internal sealed class UnavailableDisplayBrightnessHidController
    : IDisplayBrightnessHidController
{
    internal static UnavailableDisplayBrightnessHidController Instance { get; } = new();

    private UnavailableDisplayBrightnessHidController()
    {
    }

    public Task<ControlOperationResult> SetUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ControlOperationResult(
            ControlOperationStatus.Blocked,
            "The OpenBlade display-brightness HID device is unavailable."));
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public interface IWindowsBatteryReportAdapter
{
    Task<WindowsBatteryReport> ReadAsync(CancellationToken cancellationToken);
}

public sealed class WindowsBatteryHistoryViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IWindowsBatteryReportAdapter adapter;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private WindowsBatteryReport? report;
    private bool isLoading;
    private string? error;
    private int disposed;

    public WindowsBatteryHistoryViewModel(IWindowsBatteryReportAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        RefreshCommand = new AsyncRelayCommand(
            (_, token) => RefreshAsync(token),
            _ => Volatile.Read(ref disposed) == 0,
            lifetimeCancellation.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand RefreshCommand { get; }

    public WindowsBatteryReport? Report
    {
        get => report;
        private set
        {
            if (SetField(ref report, value))
            {
                OnPropertyChanged(nameof(GeneratedAtUtc));
                OnPropertyChanged(nameof(Intervals));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(HealthPercent));
            }
        }
    }

    public DateTimeOffset? GeneratedAtUtc => Report?.GeneratedAtUtc;

    public IReadOnlyList<WindowsBatteryReportInterval> Intervals =>
        Report?.Intervals ?? Array.Empty<WindowsBatteryReportInterval>();

    public WindowsBatteryStatus? Status => Report?.Status;

    public double? HealthPercent => Report?.Status.HealthPercent;

    public bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    public string? Error
    {
        get => error;
        private set => SetField(ref error, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        await refreshGate.WaitAsync(linkedCancellation.Token);
        try
        {
            IsLoading = true;
            Error = null;
            try
            {
                Report = await adapter.ReadAsync(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.WindowsBatteryHistoryViewModel",
                    "Refresh",
                    exception);
                Error = string.IsNullOrWhiteSpace(exception.Message)
                    ? TrayText.Get("Battery history is unavailable.")
                    : exception.Message;
            }
        }
        finally
        {
            IsLoading = false;
            refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        await RefreshCommand.DisposeAsync();
        // A window can call RefreshAsync directly, outside RefreshCommand.
        // Keep the gate alive until that canceled operation releases ownership.
        await refreshGate.WaitAsync();
        refreshGate.Release();
        lifetimeCancellation.Dispose();
        refreshGate.Dispose();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

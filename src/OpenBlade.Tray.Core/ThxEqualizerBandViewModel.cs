using System.ComponentModel;
using System.Globalization;

namespace OpenBlade.Tray;

public sealed class ThxEqualizerBandViewModel(int frequencyHz, double gainDb) : INotifyPropertyChanged
{
    private double confirmedGainDb = gainDb;
    private double draftGainDb = gainDb;
    private bool canEdit;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int FrequencyHz { get; } = frequencyHz;
    public string FrequencyLabel => FrequencyHz >= 1000
        ? string.Format(CultureInfo.CurrentCulture, TrayText.Get("{0} kHz"), FrequencyHz / 1000d)
        : string.Format(CultureInfo.CurrentCulture, TrayText.Get("{0} Hz"), FrequencyHz);
    public string AccessibleName => string.Format(
        CultureInfo.CurrentCulture, TrayText.Get("{0} Hz equalizer gain"), FrequencyHz);
    public string GainLabel => string.Format(CultureInfo.CurrentCulture, TrayText.Get("{0} dB"),
        draftGainDb.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture));
    public bool HasChanges => draftGainDb != confirmedGainDb;
    public bool CanEdit
    {
        get => canEdit;
        internal set
        {
            if (canEdit == value)
            {
                return;
            }
            canEdit = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEdit)));
        }
    }

    public double DraftGainDb
    {
        get => draftGainDb;
        set
        {
            if (!CanEdit || !double.IsFinite(value) || value is < -12 or > 12 || value == draftGainDb)
            {
                return;
            }
            draftGainDb = value;
            Notify();
        }
    }

    internal void Confirm(double value, bool discardDraft)
    {
        bool preserveDraft = HasChanges && !discardDraft;
        confirmedGainDb = value;
        if (!preserveDraft)
        {
            draftGainDb = value;
        }
        Notify();
    }

    internal void Discard() => DraftGainDb = confirmedGainDb;

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DraftGainDb)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChanges)));
    }
}

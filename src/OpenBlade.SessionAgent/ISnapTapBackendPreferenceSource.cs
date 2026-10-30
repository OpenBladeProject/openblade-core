using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISnapTapBackendPreferenceSource : ISnapTapPreferenceSource
{
    KeyboardInputBackend SnapTapBackend { get; }
    event Action<KeyboardInputBackend>? SnapTapBackendChanged;
}

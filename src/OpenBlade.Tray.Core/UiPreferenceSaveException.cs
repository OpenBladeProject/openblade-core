namespace OpenBlade.Tray;

public sealed class UiPreferenceSaveException : Exception
{
    public UiPreferenceSaveException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

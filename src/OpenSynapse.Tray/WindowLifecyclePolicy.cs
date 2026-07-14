namespace OpenSynapse.Tray;

public enum WindowCloseDecision
{
    Hide,
    Exit,
}

public static class WindowLifecyclePolicy
{
    public static WindowCloseDecision Decide(bool exitRequested) =>
        exitRequested ? WindowCloseDecision.Exit : WindowCloseDecision.Hide;
}

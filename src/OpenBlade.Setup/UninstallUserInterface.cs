using System.Runtime.InteropServices;

namespace OpenBlade.Setup;

internal static class UninstallUserInterface
{
    private const uint YesNo = 0x00000004;
    private const uint Ok = 0x00000000;
    private const uint IconQuestion = 0x00000020;
    private const uint IconInformation = 0x00000040;
    private const uint IconError = 0x00000010;
    private const uint TaskModal = 0x00002000;
    private const int Yes = 6;
    private const int Hide = 0;

    public static void HideOwnedConsoleWindow()
    {
        uint[] processIds = new uint[2];
        if (GetConsoleProcessList(processIds, (uint)processIds.Length) != 1)
        {
            // Do not hide a terminal that the user launched Setup from.
            return;
        }

        nint console = GetConsoleWindow();
        if (console != 0)
        {
            _ = ShowWindow(console, Hide);
        }
    }

    public static bool ConfirmPreservingProfiles() => MessageBox(
        0,
        "OpenBlade and its background service will be removed.\n\n"
            + "Your saved profiles and preferences will be kept so they are available if you reinstall.\n\n"
            + "Do you want to continue?",
        "Uninstall OpenBlade",
        YesNo | IconQuestion | TaskModal) == Yes;

    public static void ShowSuccess() => _ = MessageBox(
        0,
        "OpenBlade was removed. Your saved profiles and preferences were kept.",
        "OpenBlade uninstalled",
        Ok | IconInformation | TaskModal);

    public static void ShowFailure(string message) => _ = MessageBox(
        0,
        $"OpenBlade could not be removed.\n\n{message}",
        "OpenBlade uninstall failed",
        Ok | IconError | TaskModal);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processList,
        uint processCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);
}

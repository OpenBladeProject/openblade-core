using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

internal static class WindowsMotionSettings
{
    private const uint GetClientAreaAnimation = 0x1042;

    public static bool AreAnimationsEnabled()
    {
        try
        {
            return !SystemParametersInfo(
                    GetClientAreaAnimation,
                    0,
                    out int enabled,
                    0)
                || enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        out int value,
        uint update);
}

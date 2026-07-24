using System.Security;
using Microsoft.Win32;

namespace OpenBlade.Tray;

internal static class WindowsRegistryReader
{
    internal static int? ReadCurrentUserDword(
        string registryPath,
        string valueName) =>
        ReadCurrentUserValue(registryPath, valueName) is int value
            ? value
            : null;

    internal static byte[]? ReadCurrentUserBinary(
        string registryPath,
        string valueName) =>
        ReadCurrentUserValue(registryPath, valueName) is byte[] value
            ? (byte[])value.Clone()
            : null;

    private static object? ReadCurrentUserValue(
        string registryPath,
        string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                registryPath,
                writable: false);
            return key?.GetValue(valueName);
        }
        catch (Exception exception) when (
            exception is IOException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

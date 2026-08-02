namespace OpenBlade.SessionAgent;

/// <summary>
/// Activates the Windows Core Audio device enumerator without declaring a
/// managed COM coclass. Multiple managed coclass declarations for the same
/// CLSID can collide through the process-wide runtime-callable-wrapper cache.
/// Callers may safely query the returned object through their required Core
/// Audio interface.
/// </summary>
internal static class CoreAudioDeviceEnumerator
{
    private static readonly Guid ClassId =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static object Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Core Audio device enumeration requires Windows.");
        }

        Type enumeratorType = Type.GetTypeFromCLSID(ClassId, throwOnError: true)!;
        return Activator.CreateInstance(enumeratorType)
            ?? throw new InvalidOperationException(
                "The Core Audio device enumerator is unavailable.");
    }
}

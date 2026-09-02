using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

public sealed class WindowsApplicationRestartRegistration
{
    public const string RestartArgument = "--application-restart";

    private const uint RestartFlags = 0;
    private readonly object sync = new();
    private readonly Func<string, uint, int> register;
    private readonly Func<int> unregister;
    private bool registered;

    public WindowsApplicationRestartRegistration()
        : this(
            NativeMethods.RegisterApplicationRestart,
            NativeMethods.UnregisterApplicationRestart)
    {
    }

    internal WindowsApplicationRestartRegistration(
        Func<string, uint, int> register,
        Func<int> unregister)
    {
        this.register = register ?? throw new ArgumentNullException(nameof(register));
        this.unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public bool TryRegister()
    {
        lock (sync)
        {
            if (registered)
            {
                return true;
            }

            try
            {
                int result = register(RestartArgument, RestartFlags);
                if (result < 0)
                {
                    Trace.TraceWarning(
                        "OpenBlade Windows Application Restart registration failed (0x{0:X8}).",
                        result);
                    return false;
                }

                registered = true;
                Trace.TraceInformation(
                    "OpenBlade registered this user process with Windows Application Restart.");
                return true;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "OpenBlade Windows Application Restart registration failed: {0} (0x{1:X8}).",
                    exception.GetType().Name,
                    exception.HResult);
                return false;
            }
        }
    }

    public bool TryUnregister()
    {
        lock (sync)
        {
            if (!registered)
            {
                return true;
            }

            try
            {
                int result = unregister();
                if (result < 0)
                {
                    Trace.TraceWarning(
                        "OpenBlade Windows Application Restart unregistration failed (0x{0:X8}).",
                        result);
                    return false;
                }

                registered = false;
                Trace.TraceInformation(
                    "OpenBlade unregistered this user process from Windows Application Restart for intentional shutdown.");
                return true;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "OpenBlade Windows Application Restart unregistration failed: {0} (0x{1:X8}).",
                    exception.GetType().Name,
                    exception.HResult);
                return false;
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        internal static extern int RegisterApplicationRestart(
            string commandLineArguments,
            uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern int UnregisterApplicationRestart();
    }
}

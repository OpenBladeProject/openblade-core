using System;
using System.Text;

namespace OpenBlade.Installer.UI;

internal sealed class InstallerSetupFailure
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private InstallerSetupFailure(InstallerSetupProgress step, string error, string recoveryError)
    {
        Step = step;
        Error = error;
        RecoveryError = recoveryError;
    }

    public InstallerSetupProgress Step { get; }
    public string Error { get; }
    public string RecoveryError { get; }

    internal static bool TryParse(string[] fields, out InstallerSetupFailure? failure)
    {
        failure = null;
        string[] arguments = new string[4];
        try
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                arguments[index] = StrictUtf8.GetString(Convert.FromBase64String(fields[index + 2]));
                if (arguments[index].Length > 512 || HasControlCharacter(arguments[index]))
                {
                    return false;
                }
            }
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }

        string stepWire = "v1|" + arguments[0];
        if (arguments[0] == nameof(InstallerSetupProgressCode.CopyFile))
        {
            stepWire += "|" + fields[3];
        }
        else if (arguments[1].Length != 0)
        {
            return false;
        }
        if (arguments[0] == nameof(InstallerSetupProgressCode.Failure)
            || !InstallerSetupProgress.TryParse(stepWire, out InstallerSetupProgress? step)
            || string.IsNullOrWhiteSpace(arguments[2]))
        {
            return false;
        }

        failure = new InstallerSetupFailure(step!, arguments[2], arguments[3]);
        return true;
    }

    private static bool HasControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }
}

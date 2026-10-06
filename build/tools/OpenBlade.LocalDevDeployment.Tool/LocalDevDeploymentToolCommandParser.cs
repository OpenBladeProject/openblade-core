namespace OpenBlade.LocalDevDeploymentTool;

internal abstract record LocalDevDeploymentToolCommand;

internal sealed record StageDeploymentCommand(
    string InstallerPath,
    string Version,
    string SourceRevision,
    string KeyName) : LocalDevDeploymentToolCommand;

internal sealed record InspectKeyCommand(string KeyName) : LocalDevDeploymentToolCommand;

internal sealed record CreateKeyCommand(string KeyName) : LocalDevDeploymentToolCommand;

internal static class LocalDevDeploymentToolCommandParser
{
    private static readonly string[] StageSwitches =
    [
        "--installer",
        "--version",
        "--source-revision",
        "--key-name",
    ];

    private static readonly string[] InspectKeySwitches = ["--key-name"];

    internal static LocalDevDeploymentToolCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw Usage();
        }

        return args[0] switch
        {
            "stage" => ParseStage(args),
            "inspect-key" => ParseInspectKey(args),
            "create-key" => ParseCreateKey(args),
            _ => throw Usage(),
        };
    }

    private static StageDeploymentCommand ParseStage(string[] args)
    {
        IReadOnlyDictionary<string, string> values = ParseExactSwitches(args, StageSwitches);
        return new StageDeploymentCommand(
            values["--installer"],
            values["--version"],
            values["--source-revision"],
            values["--key-name"]);
    }

    private static InspectKeyCommand ParseInspectKey(string[] args)
    {
        IReadOnlyDictionary<string, string> values = ParseExactSwitches(
            args,
            InspectKeySwitches);
        return new InspectKeyCommand(values["--key-name"]);
    }

    private static CreateKeyCommand ParseCreateKey(string[] args)
    {
        IReadOnlyDictionary<string, string> values = ParseExactSwitches(
            args,
            InspectKeySwitches);
        return new CreateKeyCommand(values["--key-name"]);
    }

    private static IReadOnlyDictionary<string, string> ParseExactSwitches(
        string[] args,
        IReadOnlyCollection<string> requiredSwitches)
    {
        if (args.Length != 1 + (requiredSwitches.Count * 2))
        {
            throw Usage();
        }

        HashSet<string> allowed = new(requiredSwitches, StringComparer.Ordinal);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            string name = args[index];
            string value = args[index + 1];
            if (!allowed.Contains(name)
                || !values.TryAdd(name, value)
                || string.IsNullOrEmpty(value)
                || value.StartsWith("--", StringComparison.Ordinal))
            {
                throw Usage();
            }
        }

        if (values.Count != requiredSwitches.Count)
        {
            throw Usage();
        }

        return values;
    }

    private static LocalDevDeploymentToolException Usage() => new(
        "Usage: stage --installer <OpenBlade-Setup.exe> --version <x.y.z> "
        + "--source-revision <40-lower-hex> --key-name <TPM-CNG-name>; "
        + "inspect-key --key-name <TPM-CNG-name>; "
        + "or create-key --key-name <new-TPM-CNG-name>.");
}

internal sealed class LocalDevDeploymentToolException : Exception
{
    internal LocalDevDeploymentToolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

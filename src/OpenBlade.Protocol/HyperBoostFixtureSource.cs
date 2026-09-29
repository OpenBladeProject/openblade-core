using System.Reflection;

namespace OpenBlade.Protocol;

internal static class HyperBoostFixtureSource
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_HyperBoostProduction.json";

    internal const string Rz090528ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_HyperBoostProduction.json";

    internal static ReadOnlyMemory<byte>? Read() => ReadResource(ResourceName);

    internal static ReadOnlyMemory<byte>? ReadRz090528() => ReadResource(Rz090528ResourceName);

    private static ReadOnlyMemory<byte>? ReadResource(string resourceName)
    {
        Assembly assembly = typeof(HyperBoostFixtureSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

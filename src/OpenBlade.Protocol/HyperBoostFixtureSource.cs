using System.Reflection;

namespace OpenBlade.Protocol;

internal static class HyperBoostFixtureSource
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_4_00_HyperBoostProduction.json";

    internal static ReadOnlyMemory<byte>? Read()
    {
        Assembly assembly = typeof(HyperBoostFixtureSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

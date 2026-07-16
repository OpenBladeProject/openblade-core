using System.Reflection;

namespace OpenBlade.Protocol;

internal static class ProductionFixtureSource
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_3_01_BatteryProduction.json";

    internal static ReadOnlyMemory<byte>? Read()
    {
        Assembly assembly = typeof(ProductionFixtureSource).Assembly;
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

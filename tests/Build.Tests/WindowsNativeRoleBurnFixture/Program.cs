using System.Collections.Immutable;
using OpenBlade.Deployment;

if (args.Length != 4 || args[0] is not ("write" or "verify"))
{
    throw new ArgumentException("Expected write or verify, source revision, version, and payload directory.");
}

var build = new WindowsNativeBuildIdentity(args[1], Version.Parse(args[2]));
string payload = args[3];
ImmutableArray<byte> resource = WindowsNativeRoleInventory.CreateResource(
    build, name => [.. File.ReadAllBytes(Path.Combine(payload, name))]);
string inventoryPath = Path.Combine(payload, WindowsNativeRoleInventory.ResourceName);
if (args[0] == "write")
{
    using FileStream output = File.Open(inventoryPath, FileMode.CreateNew, FileAccess.Write);
    output.Write(resource.AsSpan());
}
else if (!resource.AsSpan().SequenceEqual(File.ReadAllBytes(inventoryPath)))
{
    throw new InvalidDataException("The inventory does not describe the extracted build and role hashes.");
}

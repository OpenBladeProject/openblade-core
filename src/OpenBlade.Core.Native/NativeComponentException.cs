namespace OpenBlade.Core.Native;

/// <summary>A bounded, path-free failure of the required first-party component.</summary>
public sealed class NativeComponentException : Exception
{
    public NativeComponentException(int category)
        : base($"OpenBlade's required native component is unavailable (category {category}). Repair or rebuild OpenBlade.")
    {
        Category = category;
    }

    public NativeComponentException(NativeBoundaryError error) : this((int)error.Kind) { }

    public int Category { get; }
}

namespace OpenBlade.Contracts;

/// <summary>
/// One host-rendered color applied uniformly to every addressable keyboard slot.
/// This shape has no physical key-coordinate or layout-mask dependency.
/// </summary>
public sealed record UniformLightingFrame(RgbColor Color);

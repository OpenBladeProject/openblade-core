using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("OpenBlade.Capture")]

namespace OpenBlade.Service;

internal static class CoolingPadDeveloperValidation
{
    internal static ICoolingPadControlPortFactory CreatePortFactory() =>
        CoolingPadProtocolPortFactory.CreateForDeveloperValidation();
}

using System.Security.AccessControl;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuVoltageWriteDeadlineTests
{
    [Fact]
    public void ProtectedDeadlineAllowsEnrolledReadAccessButNotUserMutation()
    {
        WindowsCpuVoltageWriteDeadline.RequireProtection(new RawSecurityDescriptor(
            "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)"));
        Assert.Throws<IOException>(() => WindowsCpuVoltageWriteDeadline.RequireProtection(new RawSecurityDescriptor(
            "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GW;;;BU)")));
        Assert.Throws<IOException>(() => WindowsCpuVoltageWriteDeadline.RequireProtection(new RawSecurityDescriptor(
            "O:BUD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)")));
        Assert.Throws<IOException>(() => WindowsCpuVoltageWriteDeadline.RequireProtection(new RawSecurityDescriptor(
            ControlFlags.SelfRelative, new("S-1-5-32-544"), null, null, null)));
    }
}

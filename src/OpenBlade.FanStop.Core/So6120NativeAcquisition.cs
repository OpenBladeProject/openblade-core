using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenBlade.FanStop;

// Fixed reviewed disk pin and zero-access device open shared by attended and product owners.
internal static class So6120NativeAcquisition
{
    internal static FileStream PinDriverFile(So6120DriverMetadata driver)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            So6120SupervisedAdmission.DriverRelativePath);
        if (!Path.IsPathFullyQualified(expected) || !string.Equals(driver.FilePath, expected, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidOperationException("The reviewed InpOut disk path is unavailable."); }
        var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != So6120SupervisedAdmission.DriverBytes
                || Convert.ToHexString(SHA256.HashData(file)) != So6120SupervisedAdmission.DriverHash)
            { throw new InvalidOperationException("The locked InpOut disk file differs from the reviewed driver."); }
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static So6120OwnedInpOutHandle OpenDevice()
    {
        const uint shareReadWriteDelete = 7;
        const uint openExisting = 3;
        So6120OwnedInpOutHandle handle = CreateFileW(@"\\.\inpoutx64", 0, shareReadWriteDelete, 0, openExisting, 0, 0);
        int error = Marshal.GetLastWin32Error();
        if (!handle.IsInvalid) { return handle; }
        handle.Dispose();
        throw new Win32Exception(error, "The fixed zero-access InpOut open failed.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern So6120OwnedInpOutHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
}

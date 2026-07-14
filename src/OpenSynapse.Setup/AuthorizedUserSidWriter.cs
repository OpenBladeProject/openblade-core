using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenSynapse.Setup;

public sealed record AuthorizedSidWriteRequest(string Path, string Sid);

public interface IAuthorizedUserSidWriter
{
    Task WriteAsync(AuthorizedSidWriteRequest request, CancellationToken cancellationToken);
}

public interface IAuthorizedSidFileOperations
{
    void CreateDirectory(string path);
    Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken);
    void RestrictToSystemAndAdministrators(string path);
    void Replace(string source, string destination);
    void DeleteIfExists(string path);
}

public sealed class AtomicAuthorizedUserSidWriter(
    IAuthorizedSidFileOperations? files = null) : IAuthorizedUserSidWriter
{
    private readonly IAuthorizedSidFileOperations files = files ?? new WindowsAuthorizedSidFileOperations();

    public async Task WriteAsync(
        AuthorizedSidWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string directory = Path.GetDirectoryName(request.Path)
            ?? throw new ArgumentException("The authorized SID path must have a parent directory.", nameof(request));
        string temporaryPath = Path.Combine(
            directory,
            $"authorized-user.{Guid.NewGuid():N}.tmp");
        bool replaced = false;
        try
        {
            files.CreateDirectory(directory);
            await files.WriteAllTextAsync(temporaryPath, request.Sid, cancellationToken);
            files.RestrictToSystemAndAdministrators(temporaryPath);
            files.Replace(temporaryPath, request.Path);
            replaced = true;
        }
        finally
        {
            if (!replaced)
            {
                files.DeleteIfExists(temporaryPath);
            }
        }
    }
}

internal sealed class WindowsAuthorizedSidFileOperations : IAuthorizedSidFileOperations
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, cancellationToken);

    public void RestrictToSystemAndAdministrators(string path)
    {
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            localSystem,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public void Replace(string source, string destination) =>
        File.Move(source, destination, overwrite: true);

    public void DeleteIfExists(string path) => File.Delete(path);
}

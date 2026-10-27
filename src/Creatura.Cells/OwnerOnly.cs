using System.Security.AccessControl;
using System.Security.Principal;

namespace Creatura.Cells;

/// <summary>
/// Files and directories only their owner can read: mode 700 / 600 on Unix, set at creation so there is no moment
/// when the file is readable by others; on Windows an access list with the current user alone, inheritance cut.
/// Private keys, the vessel salt and the ledger are written through here.
/// </summary>
public static class OwnerOnly
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode FileMode_ = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode);
        }
    }

    /// <summary>Writes a file that must not exist yet. Throws <see cref="IOException"/> when it does.</summary>
    public static void WriteNew(string path, byte[] content)
    {
        CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = Open(path, FileMode.CreateNew);
        stream.Write(content);
    }

    /// <summary>Writes a file whole, replacing what was there: a sibling temporary file renamed over it.</summary>
    public static void Replace(string path, byte[] content)
    {
        var full = Path.GetFullPath(path);
        CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        using (var stream = Open(temporary, FileMode.CreateNew))
        {
            stream.Write(content);
        }
        File.Move(temporary, full, overwrite: true);
    }

    /// <summary>True when nobody but the owner can read or write the file (always true on Windows, where the access list is inherited).</summary>
    public static bool IsOwnerOnly(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) == 0;

    private static FileStream Open(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode_;
        return new FileStream(path, options);
    }
}

namespace FateAndFridge.Api.Services;

/// <summary>
/// The filesystem operations a restore may use to replace the contents of the
/// live <c>images/</c> and <c>config/dp-keys/</c> directories (docs/API.md §18;
/// docs/M13_VERIFICATION.md).
/// </summary>
/// <remarks>
/// <para>
/// The interface deliberately has **no rename or move operation**. In the
/// container both live directories are bind-mount roots, and the kernel refuses
/// to rename a mount root (<c>EXDEV</c> / "Resource busy") even when source and
/// destination are on the same filesystem. The swap therefore replaces the
/// contents of the mount root, never the mount root itself.
/// </para>
/// <para>
/// It exists as a seam so the integration tests can inject a deterministic
/// mid-copy failure; production has exactly one implementation.
/// </para>
/// </remarks>
public interface IRestoreFileOperations
{
    /// <summary>
    /// Recursively copies every regular file and directory under
    /// <paramref name="sourceDirectory"/> into
    /// <paramref name="destinationDirectory"/>, keeping relative paths and file
    /// modes. Symbolic links are not followed. The source must exist; the
    /// destination is created when missing and existing files are overwritten.
    /// </summary>
    void CopyDirectoryContents(string sourceDirectory, string destinationDirectory);

    /// <summary>
    /// Deletes every child of <paramref name="directory"/> but keeps the
    /// directory itself, so a bind-mount root is never removed.
    /// </summary>
    void ClearDirectoryContents(string directory);

    /// <summary>Deletes a directory tree when it exists.</summary>
    void DeleteDirectory(string directory);

    /// <summary>
    /// Reads the Unix mode of <paramref name="directory"/>, or null when the host
    /// has no Unix modes or the mode cannot be read. A restore captures the mode
    /// of each live target before it replaces the target's contents, so the
    /// mount-root permissions survive the swap.
    /// </summary>
    UnixFileMode? GetDirectoryMode(string directory);

    /// <summary>
    /// Re-applies a mode captured by <see cref="GetDirectoryMode"/>. Best effort:
    /// the contents are already in place, so a mode that cannot be re-applied is
    /// not fatal. Ownership is deliberately never touched.
    /// </summary>
    void SetDirectoryMode(string directory, UnixFileMode mode);
}

/// <summary>
/// The production <see cref="IRestoreFileOperations"/>: plain <see cref="Directory"/>,
/// <see cref="File"/> copy/delete calls, with Unix file modes carried over.
/// </summary>
internal sealed class SystemRestoreFileOperations : IRestoreFileOperations
{
    private static readonly EnumerationOptions Tree = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        // Never descend through (or copy) a symbolic link.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions Children = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    public void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", Tree))
        {
            Directory.CreateDirectory(
                Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", Tree))
        {
            var target = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, file));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            CopyFileMode(file, target);
        }
    }

    public void ClearDirectoryContents(string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (var child in Directory.EnumerateDirectories(directory, "*", Children))
        {
            Directory.Delete(child, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", Children))
        {
            File.Delete(file);
        }
    }

    public void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Reads the directory's mode, or null when it cannot be read. The swap
    /// captures it before emptying the directory and re-applies it afterwards,
    /// because the deployment's <c>images/</c> is a bind-mounted SMB share whose
    /// mode is set by the mount options, not by this application.
    /// </summary>
    public UnixFileMode? GetDirectoryMode(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.GetUnixFileMode(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to re-apply: the copy leaves the current mode in place.
            return null;
        }
    }

    /// <summary>
    /// Puts a captured directory mode back. Best effort only: the contents are
    /// already copied, so a mode that cannot be re-applied must not fail the
    /// restore. Ownership and group are left exactly as the mount set them.
    /// </summary>
    public void SetDirectoryMode(string directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory, mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }


    /// <summary>
    /// Keeps the source mode, which matters for the Data Protection key files
    /// (written 0600). A mode that cannot be read or applied is not fatal: the
    /// copy itself is complete and the default mode survives.
    /// </summary>
    private static void CopyFileMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}

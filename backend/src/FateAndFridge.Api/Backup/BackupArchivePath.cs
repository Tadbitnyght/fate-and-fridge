using System.Text;

namespace FateAndFridge.Api.Backup;

/// <summary>
/// Path-traversal guard shared by the archive writer and reader
/// (docs/SECURITY.md §12). Names are tar paths: relative, forward-slash
/// separated, and never absolute. The reader never writes to disk, but it
/// rejects an unsafe name before exposing an entry so a later extraction step
/// cannot be tricked.
/// </summary>
public static class BackupArchivePath
{
    /// <summary>Maximum UTF-8 byte length of an entry name.</summary>
    public const int MaximumNameBytes = 512;

    /// <summary>
    /// Validates and normalizes an entry name. A single trailing slash is
    /// accepted only for directory entries and is removed from the result.
    /// </summary>
    /// <exception cref="BackupArchiveException">
    /// <c>PATH_UNSAFE</c> for an empty name, an absolute path, a backslash, a
    /// control character, a <c>.</c>/<c>..</c>/empty segment, a drive-letter
    /// prefix, or a name above <see cref="MaximumNameBytes"/>.
    /// </exception>
    public static string ValidateEntryName(string? name, bool allowTrailingSlash = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw BackupArchiveException.PathUnsafe("A backup entry name cannot be empty.");
        }

        var value = name;
        if (value.EndsWith('/'))
        {
            if (!allowTrailingSlash)
            {
                throw BackupArchiveException.PathUnsafe(
                    "A backup file entry name cannot end with a slash.");
            }

            value = value[..^1];
            if (value.Length == 0)
            {
                throw BackupArchiveException.PathUnsafe("A backup entry name cannot be empty.");
            }
        }

        if (value[0] is '/' or '\\')
        {
            throw BackupArchiveException.PathUnsafe(
                "A backup entry name cannot be an absolute path.");
        }

        if (value.Contains('\\'))
        {
            throw BackupArchiveException.PathUnsafe(
                "A backup entry name cannot contain a backslash.");
        }

        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            throw BackupArchiveException.PathUnsafe(
                "A backup entry name cannot contain a drive-letter prefix.");
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw BackupArchiveException.PathUnsafe(
                    "A backup entry name cannot contain control characters.");
            }
        }

        if (Encoding.UTF8.GetByteCount(value) > MaximumNameBytes)
        {
            throw BackupArchiveException.PathUnsafe(
                $"A backup entry name cannot exceed {MaximumNameBytes} UTF-8 bytes.");
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw BackupArchiveException.PathUnsafe(
                    "A backup entry name cannot contain an empty, '.', or '..' path segment.");
            }
        }

        return value;
    }
}

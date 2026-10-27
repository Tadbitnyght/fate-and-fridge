namespace FateAndFridge.Api.Backup;

/// <summary>
/// Stable error codes for the backup archive format (docs/M13_PLAN.md §1.4,
/// §4). M13-03/M13-04 map these to the API error envelope without parsing
/// messages.
/// </summary>
public static class BackupArchiveErrorCodes
{
    /// <summary>The archive is not a Fate &amp; Fridge backup or uses an unsupported version.</summary>
    public const string FormatUnsupported = "FORMAT_UNSUPPORTED";

    /// <summary>Authentication, truncation, reordering, or manifest verification failed.</summary>
    public const string IntegrityFailed = "INTEGRITY_FAILED";

    /// <summary>The recovery key does not match the archive's key-check tag.</summary>
    public const string KeyMismatch = "KEY_MISMATCH";

    /// <summary>An entry name or the entry set is unsafe (traversal, duplicates, limits).</summary>
    public const string PathUnsafe = "PATH_UNSAFE";
}

/// <summary>
/// A backup archive could not be written or read. <see cref="Code"/> is one of
/// <see cref="BackupArchiveErrorCodes"/>; the message is safe to log (it never
/// contains key material or plaintext).
/// </summary>
public sealed class BackupArchiveException : Exception
{
    public BackupArchiveException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>One of <see cref="BackupArchiveErrorCodes"/>.</summary>
    public string Code { get; }

    public static BackupArchiveException FormatUnsupported(string message) =>
        new(BackupArchiveErrorCodes.FormatUnsupported, message);

    public static BackupArchiveException IntegrityFailed(string message, Exception? innerException = null) =>
        new(BackupArchiveErrorCodes.IntegrityFailed, message, innerException);

    public static BackupArchiveException KeyMismatch() =>
        new(
            BackupArchiveErrorCodes.KeyMismatch,
            "The recovery key does not match this backup archive.");

    public static BackupArchiveException PathUnsafe(string message) =>
        new(BackupArchiveErrorCodes.PathUnsafe, message);
}

namespace FateAndFridge.Api.Domain;

/// <summary>
/// Metadata for one backup archive. The archive file itself lives in the mounted
/// <c>/data/backups</c> directory; only its identifying facts are stored here.
/// <see cref="Checksum"/> is the lowercase SHA-256 hex digest of the encrypted
/// file, so a download can be verified without decrypting it.
/// </summary>
public sealed class BackupMetadata
{
    public long Id { get; set; }

    /// <summary>File name inside <c>/data/backups</c> (or <c>safety/</c>); unique.</summary>
    public required string Filename { get; set; }

    /// <summary>Application version that produced the archive.</summary>
    public required string AppVersion { get; set; }

    /// <summary>Applied migration id (or count) at archive creation.</summary>
    public required string SchemaVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Nullable because a system safety backup has no acting user.</summary>
    public long? CreatedByUserId { get; set; }

    /// <summary>Lowercase 64-character SHA-256 hex digest of the archive file.</summary>
    public required string Checksum { get; set; }

    public long SizeBytes { get; set; }

    public BackupKind Kind { get; set; }

    public BackupStatus Status { get; set; } = BackupStatus.Pending;

    /// <summary>Failure detail for a <c>failed</c> archive, otherwise null.</summary>
    public string? Error { get; set; }
}

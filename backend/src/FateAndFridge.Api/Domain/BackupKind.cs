namespace FateAndFridge.Api.Domain;

/// <summary>
/// Why a backup archive exists. Stored as snake_case
/// text: <c>manual</c> for an Admin-requested backup, <c>safety</c> for the
/// mandatory pre-restore snapshot. Manual backups are never pruned
/// automatically; safety backups keep only the most recent five.
/// </summary>
public enum BackupKind
{
    Manual,
    Safety,
}

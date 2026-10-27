namespace FateAndFridge.Api.Domain;

/// <summary>
/// Lifecycle of one backup archive (docs/M13_PLAN.md §1.7). Stored as
/// snake_case text. A row starts <c>pending</c> and becomes <c>completed</c> or
/// <c>failed</c>; a restore marks its archive <c>restored</c>, and an explicit
/// Admin deletion marks it <c>deleted</c> (the file is removed too).
/// </summary>
public enum BackupStatus
{
    Pending,
    Completed,
    Failed,
    Restored,
    Deleted,
}

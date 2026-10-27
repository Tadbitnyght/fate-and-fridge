namespace FateAndFridge.Api.Domain;

/// <summary>
/// One granted permission key for a user (for example <c>calendar.edit</c>).
/// Unique per user and permission key.
/// </summary>
public sealed class UserPermission
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public required string PermissionKey { get; set; }
}

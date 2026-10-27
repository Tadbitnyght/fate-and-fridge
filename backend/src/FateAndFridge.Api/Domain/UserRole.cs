namespace FateAndFridge.Api.Domain;

/// <summary>
/// Authorization role, stored as text (<c>admin</c> or <c>member</c>) per
/// docs/DATABASE.md §1.
/// </summary>
public enum UserRole
{
    Admin,
    Member,
}

namespace FateAndFridge.Api.Domain;

/// <summary>
/// Household authentication mode, stored as text (<c>single_user</c> or
/// <c>multiuser</c>) per docs/DATABASE.md §1.
/// </summary>
public enum AuthMode
{
    SingleUser,
    Multiuser,
}

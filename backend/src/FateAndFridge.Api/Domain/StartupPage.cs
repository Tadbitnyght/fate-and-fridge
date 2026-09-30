namespace FateAndFridge.Api.Domain;

/// <summary>
/// Default page shown after sign-in, stored as text (<c>calendar</c> or
/// <c>dashboard</c>) per docs/DATABASE.md §1.
/// </summary>
public enum StartupPage
{
    Calendar,
    Dashboard,
}

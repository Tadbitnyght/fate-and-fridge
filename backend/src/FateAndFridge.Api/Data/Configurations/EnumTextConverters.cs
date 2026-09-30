using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>
/// Text conversions for enum-like columns. Values are stable snake_case strings
/// (docs/DATABASE.md §1); unrecognized values fail loudly rather than being
/// silently coerced. The converters call static methods so the switch-based
/// logic stays compiled code (expression trees cannot contain switch or throw
/// expressions).
/// </summary>
internal static class EnumTextConverters
{
    public static ValueConverter<UserRole, string> UserRoleConverter { get; } =
        new ValueConverter<UserRole, string>(
            role => UserRoleToText(role),
            text => UserRoleFromText(text));

    public static ValueConverter<AuthMode, string> AuthModeConverter { get; } =
        new ValueConverter<AuthMode, string>(
            mode => AuthModeToText(mode),
            text => AuthModeFromText(text));

    public static ValueConverter<StartupPage, string> StartupPageConverter { get; } =
        new ValueConverter<StartupPage, string>(
            page => StartupPageToText(page),
            text => StartupPageFromText(text));

    internal static string UserRoleToText(UserRole role) => role switch
    {
        UserRole.Admin => "admin",
        UserRole.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown user role."),
    };

    internal static bool TryUserRoleFromText(string? text, out UserRole role)
    {
        switch (text)
        {
            case "admin":
                role = UserRole.Admin;
                return true;
            case "member":
                role = UserRole.Member;
                return true;
            default:
                role = default;
                return false;
        }
    }

    private static UserRole UserRoleFromText(string text) =>
        TryUserRoleFromText(text, out var role)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown user role text.");

    internal static string AuthModeToText(AuthMode mode) => mode switch
    {
        AuthMode.SingleUser => "single_user",
        AuthMode.Multiuser => "multiuser",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown auth mode."),
    };

    private static AuthMode AuthModeFromText(string text) => text switch
    {
        "single_user" => AuthMode.SingleUser,
        "multiuser" => AuthMode.Multiuser,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown auth mode text."),
    };

    internal static string StartupPageToText(StartupPage page) => page switch
    {
        StartupPage.Calendar => "calendar",
        StartupPage.Dashboard => "dashboard",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown startup page."),
    };

    internal static bool TryStartupPageFromText(string text, out StartupPage page)
    {
        switch (text)
        {
            case "calendar":
                page = StartupPage.Calendar;
                return true;
            case "dashboard":
                page = StartupPage.Dashboard;
                return true;
            default:
                page = default;
                return false;
        }
    }

    private static StartupPage StartupPageFromText(string text) =>
        TryStartupPageFromText(text, out var page)
            ? page
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown startup page text.");
}

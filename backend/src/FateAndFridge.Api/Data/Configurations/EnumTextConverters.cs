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

    public static ValueConverter<UnitDimension, string> UnitDimensionConverter { get; } =
        new ValueConverter<UnitDimension, string>(
            dimension => UnitDimensionToText(dimension),
            text => UnitDimensionFromText(text));

    public static ValueConverter<RecipeStatus, string> RecipeStatusConverter { get; } =
        new ValueConverter<RecipeStatus, string>(
            status => RecipeStatusToText(status),
            text => RecipeStatusFromText(text));

    public static ValueConverter<CalendarEntryType, string> CalendarEntryTypeConverter { get; } =
        new ValueConverter<CalendarEntryType, string>(
            type => CalendarEntryTypeToText(type),
            text => CalendarEntryTypeFromText(text));

    public static ValueConverter<BackupKind, string> BackupKindConverter { get; } =
        new ValueConverter<BackupKind, string>(
            kind => BackupKindToText(kind),
            text => BackupKindFromText(text));

    public static ValueConverter<BackupStatus, string> BackupStatusConverter { get; } =
        new ValueConverter<BackupStatus, string>(
            status => BackupStatusToText(status),
            text => BackupStatusFromText(text));

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

    internal static bool TryAuthModeFromText(string? text, out AuthMode mode)
    {
        switch (text)
        {
            case "single_user":
                mode = AuthMode.SingleUser;
                return true;
            case "multiuser":
                mode = AuthMode.Multiuser;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static AuthMode AuthModeFromText(string text) =>
        TryAuthModeFromText(text, out var mode)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown auth mode text.");

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

    internal static string UnitDimensionToText(UnitDimension dimension) => dimension switch
    {
        UnitDimension.Mass => "mass",
        UnitDimension.Volume => "volume",
        UnitDimension.Count => "count",
        UnitDimension.Nonconvertible => "nonconvertible",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown unit dimension."),
    };

    internal static bool TryUnitDimensionFromText(string? text, out UnitDimension dimension)
    {
        switch (text)
        {
            case "mass":
                dimension = UnitDimension.Mass;
                return true;
            case "volume":
                dimension = UnitDimension.Volume;
                return true;
            case "count":
                dimension = UnitDimension.Count;
                return true;
            case "nonconvertible":
                dimension = UnitDimension.Nonconvertible;
                return true;
            default:
                dimension = default;
                return false;
        }
    }

    private static UnitDimension UnitDimensionFromText(string text) =>
        TryUnitDimensionFromText(text, out var dimension)
            ? dimension
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown unit dimension text.");

    internal static string RecipeStatusToText(RecipeStatus status) => status switch
    {
        RecipeStatus.Normal => "normal",
        RecipeStatus.ManualOnly => "manual_only",
        RecipeStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown recipe status."),
    };

    internal static bool TryRecipeStatusFromText(string? text, out RecipeStatus status)
    {
        switch (text)
        {
            case "normal":
                status = RecipeStatus.Normal;
                return true;
            case "manual_only":
                status = RecipeStatus.ManualOnly;
                return true;
            case "retired":
                status = RecipeStatus.Retired;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static RecipeStatus RecipeStatusFromText(string text) =>
        TryRecipeStatusFromText(text, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown recipe status text.");

    internal static string CalendarEntryTypeToText(CalendarEntryType type) => type switch
    {
        CalendarEntryType.Recipe => "recipe",
        CalendarEntryType.EatOut => "eat_out",
        CalendarEntryType.FendForYourself => "fend_for_yourself",
        CalendarEntryType.HostFamilyDinner => "host_family_dinner",
        CalendarEntryType.GoToFamilyDinner => "go_to_family_dinner",
        CalendarEntryType.Holiday => "holiday",
        CalendarEntryType.Misc => "misc",
        CalendarEntryType.Vacation => "vacation",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown calendar entry type."),
    };

    internal static bool TryCalendarEntryTypeFromText(string? text, out CalendarEntryType type)
    {
        switch (text)
        {
            case "recipe":
                type = CalendarEntryType.Recipe;
                return true;
            case "eat_out":
                type = CalendarEntryType.EatOut;
                return true;
            case "fend_for_yourself":
                type = CalendarEntryType.FendForYourself;
                return true;
            case "host_family_dinner":
                type = CalendarEntryType.HostFamilyDinner;
                return true;
            case "go_to_family_dinner":
                type = CalendarEntryType.GoToFamilyDinner;
                return true;
            case "holiday":
                type = CalendarEntryType.Holiday;
                return true;
            case "misc":
                type = CalendarEntryType.Misc;
                return true;
            case "vacation":
                type = CalendarEntryType.Vacation;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static CalendarEntryType CalendarEntryTypeFromText(string text) =>
        TryCalendarEntryTypeFromText(text, out var type)
            ? type
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown calendar entry type text.");

    internal static string BackupKindToText(BackupKind kind) => kind switch
    {
        BackupKind.Manual => "manual",
        BackupKind.Safety => "safety",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown backup kind."),
    };

    internal static bool TryBackupKindFromText(string? text, out BackupKind kind)
    {
        switch (text)
        {
            case "manual":
                kind = BackupKind.Manual;
                return true;
            case "safety":
                kind = BackupKind.Safety;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static BackupKind BackupKindFromText(string text) =>
        TryBackupKindFromText(text, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown backup kind text.");

    internal static string BackupStatusToText(BackupStatus status) => status switch
    {
        BackupStatus.Pending => "pending",
        BackupStatus.Completed => "completed",
        BackupStatus.Failed => "failed",
        BackupStatus.Restored => "restored",
        BackupStatus.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown backup status."),
    };

    internal static bool TryBackupStatusFromText(string? text, out BackupStatus status)
    {
        switch (text)
        {
            case "pending":
                status = BackupStatus.Pending;
                return true;
            case "completed":
                status = BackupStatus.Completed;
                return true;
            case "failed":
                status = BackupStatus.Failed;
                return true;
            case "restored":
                status = BackupStatus.Restored;
                return true;
            case "deleted":
                status = BackupStatus.Deleted;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static BackupStatus BackupStatusFromText(string text) =>
        TryBackupStatusFromText(text, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown backup status text.");
}

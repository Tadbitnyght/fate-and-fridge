using System.Collections.ObjectModel;

namespace FateAndFridge.Api.Auth.Permissions;

/// <summary>
/// The seven permission keys from docs/DATABASE.md §2, with a canonical
/// ordering so session payloads, enforcement, and tests all agree.
/// </summary>
public static class PermissionKeys
{
    public const string CalendarView = "calendar.view";

    public const string CalendarEdit = "calendar.edit";

    public const string GroceriesView = "groceries.view";

    public const string GroceriesEdit = "groceries.edit";

    public const string RecipesView = "recipes.view";

    public const string RecipesEdit = "recipes.edit";

    public const string ReferenceDataEdit = "reference_data.edit";

    private static readonly string[] AllKeysArray =
    [
        CalendarView,
        CalendarEdit,
        GroceriesView,
        GroceriesEdit,
        RecipesView,
        RecipesEdit,
        ReferenceDataEdit,
    ];

    /// <summary>All seven keys in canonical order.</summary>
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(AllKeysArray);

    /// <summary>
    /// Default grants for a newly created member: view-only — <c>calendar.view</c>, <c>groceries.view</c>
    /// <c>recipes.view</c> in canonical order.
    /// </summary>
    public static readonly IReadOnlyList<string> MemberViewOnly = Array.AsReadOnly(
    [
        CalendarView,
        GroceriesView,
        RecipesView,
    ]);

    /// <summary>True when <paramref name="key"/> is one of the known keys.</summary>
    public static bool IsKnown(string? key) =>
        key is not null && AllKeysArray.Contains(key, StringComparer.Ordinal);
}

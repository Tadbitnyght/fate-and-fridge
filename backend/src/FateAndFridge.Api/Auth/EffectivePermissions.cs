using System.Collections.ObjectModel;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Minimal effective-permission computation for the M1-07 session responses
/// (docs/DATABASE.md §2): Admins hold all seven keys; members hold their
/// granted keys plus the matching view key for every granted edit key. Unknown
/// keys are ignored, and results follow the canonical key order so responses
/// are stable.
/// </summary>
/// <remarks>
/// M1-09 owns the full permission evaluator and endpoint enforcement helpers;
/// until then this helper serves only <c>GET /auth/session</c> and the login
/// response.
/// </remarks>
public static class EffectivePermissions
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

    /// <summary>All seven permission keys in canonical order.</summary>
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(AllKeysArray);

    private static readonly ReadOnlyDictionary<string, string> ImpliedViewKeys =
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CalendarEdit] = CalendarView,
            [GroceriesEdit] = GroceriesView,
            [RecipesEdit] = RecipesView,
        });

    /// <summary>
    /// Effective permission keys for <paramref name="role"/>, with explicit
    /// member grants applied as edit-implies-view.
    /// </summary>
    public static IReadOnlyList<string> For(UserRole role, IEnumerable<string>? grantedKeys = null)
    {
        if (role == UserRole.Admin)
        {
            return All;
        }

        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in grantedKeys ?? [])
        {
            if (!AllKeysArray.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            effective.Add(key);
            if (ImpliedViewKeys.TryGetValue(key, out var impliedViewKey))
            {
                effective.Add(impliedViewKey);
            }
        }

        return Array.AsReadOnly(AllKeysArray.Where(effective.Contains).ToArray());
    }
}

using System.Collections.ObjectModel;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth.Permissions;

/// <summary>
/// The single authoritative effective-permission computation
/// (docs/ARCHITECTURE.md §10, docs/DATABASE.md §2): the single-user system
/// actor and Admins hold all seven keys; members hold their granted keys plus
/// the matching view key for every granted edit key. Unknown keys are ignored
/// and results follow the canonical key order.
/// </summary>
public static class PermissionEvaluator
{
    private static readonly ReadOnlyDictionary<string, string> ImpliedViewKeys =
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PermissionKeys.CalendarEdit] = PermissionKeys.CalendarView,
            [PermissionKeys.GroceriesEdit] = PermissionKeys.GroceriesView,
            [PermissionKeys.RecipesEdit] = PermissionKeys.RecipesView,
        });

    /// <summary>
    /// Effective keys for the actor: system/Admin bypass member grants,
    /// members are evaluated from their granted keys.
    /// </summary>
    public static IReadOnlyList<string> For(CurrentActor actor, IEnumerable<string>? grantedKeys = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.IsSystem || actor.Role == UserRole.Admin
            ? PermissionKeys.All
            : ForMember(grantedKeys);
    }

    /// <summary>
    /// Effective keys for a member: known grants plus edit-implies-view,
    /// filtered to known keys and returned in canonical order.
    /// </summary>
    public static IReadOnlyList<string> ForMember(IEnumerable<string>? grantedKeys = null)
    {
        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in grantedKeys ?? [])
        {
            if (!PermissionKeys.IsKnown(key))
            {
                continue;
            }

            effective.Add(key);
            if (ImpliedViewKeys.TryGetValue(key, out var impliedViewKey))
            {
                effective.Add(impliedViewKey);
            }
        }

        return Array.AsReadOnly(PermissionKeys.All.Where(effective.Contains).ToArray());
    }
}

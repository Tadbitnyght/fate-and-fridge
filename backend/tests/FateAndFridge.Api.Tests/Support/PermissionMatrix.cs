using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Test-side oracle for the M1-09 permission matrix (docs/DATABASE.md §2,
/// docs/ARCHITECTURE.md §10). It recomputes expected effective sets from the
/// specification instead of calling the production evaluator, so tests compare
/// enforcement against the documented rules. Reusable by later issues (M1-10
/// user management, M1-15 verification).
/// </summary>
public static class PermissionMatrix
{
    /// <summary>Canonical key order from docs/DATABASE.md §2.</summary>
    public static readonly IReadOnlyList<string> AllKeys =
    [
        "calendar.view",
        "calendar.edit",
        "groceries.view",
        "groceries.edit",
        "recipes.view",
        "recipes.edit",
        "reference_data.edit",
    ];

    public const string AdminProbePath = "/api/v1/permission-probe/admin";

    public const string AuthenticatedProbePath = "/api/v1/permission-probe/any-authenticated";

    public const string AdminThenPermissionProbePath =
        "/api/v1/permission-probe/chained/admin-then-permission";

    public const string PermissionThenAdminProbePath =
        "/api/v1/permission-probe/chained/permission-then-admin";

    private static readonly Dictionary<string, string> ImpliedViewKeys = new(StringComparer.Ordinal)
    {
        ["calendar.edit"] = "calendar.view",
        ["groceries.edit"] = "groceries.view",
        ["recipes.edit"] = "recipes.view",
    };

    /// <summary>Spec-defined effective keys for a role and its granted rows.</summary>
    public static IReadOnlyList<string> EffectiveKeys(UserRole role, IEnumerable<string> grantedKeys)
    {
        if (role == UserRole.Admin)
        {
            return AllKeys;
        }

        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in grantedKeys)
        {
            if (!AllKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            effective.Add(key);
            if (ImpliedViewKeys.TryGetValue(key, out var impliedViewKey))
            {
                effective.Add(impliedViewKey);
            }
        }

        return AllKeys.Where(effective.Contains).ToArray();
    }

    /// <summary>Spec-defined effective keys for a member with the given grants.</summary>
    public static IReadOnlyList<string> MemberKeys(params string[] grantedKeys) =>
        EffectiveKeys(UserRole.Member, grantedKeys);

    /// <summary>Probe route that requires <paramref name="permissionKey"/>.</summary>
    public static string ProbePath(string permissionKey) =>
        $"/api/v1/permission-probe/{permissionKey}";
}

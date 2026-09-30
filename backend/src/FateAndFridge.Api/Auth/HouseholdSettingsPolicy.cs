using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Interim authorization policy for the household settings endpoints
/// (docs/API.md §21): any authenticated caller may read, and only the
/// single-user system actor or an Admin may write. M1-09 replaces this with
/// the permission evaluator; the endpoint contract stays the same.
/// </summary>
public static class HouseholdSettingsPolicy
{
    /// <summary>Any authenticated actor (including the system actor) may read.</summary>
    public static bool CanRead(CurrentActor? actor) => actor is not null;

    /// <summary>Only the system actor or an Admin may change household settings.</summary>
    public static bool CanWrite(CurrentActor? actor) =>
        actor is not null && (actor.IsSystem || actor.Role == UserRole.Admin);
}

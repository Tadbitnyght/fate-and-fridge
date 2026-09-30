namespace FateAndFridge.Api.Services;

/// <summary>
/// Advisory transaction-lock keys that serialize mutations affecting the
/// admin-count invariant (M1-10). Taken inside the mutation transaction so the
/// lock is released on commit/rollback.
/// </summary>
internal static class UserManagementLock
{
    /// <summary>Spells "__ADMIN": last-active-Admin and auth-mode invariants.</summary>
    public const long AdminSafetyKey = 0x5F5F41444D494E;
}

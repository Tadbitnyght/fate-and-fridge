namespace FateAndFridge.Api.Domain;

/// <summary>
/// Household user. Users are disabled or retired rather than hard-deleted
/// timestamps are set by the application and are always UTC.
/// </summary>
public sealed class User
{
    public long Id { get; set; }

    public required string Username { get; set; }

    /// <summary>Lowercase, trimmed lookup form; unique.</summary>
    public required string NormalizedUsername { get; set; }

    /// <summary>PHC-encoded password hash, for example Argon2id. Never a plaintext password.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Set by an Admin reset; cleared after a successful password change.</summary>
    public bool MustChangePassword { get; set; }

    public UserRole Role { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Consecutive failures since the last successful login.</summary>
    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LastFailedLoginAt { get; set; }

    /// <summary>Lockouts inside the current lockout window.</summary>
    public int LockoutCount { get; set; }

    public DateTimeOffset? LockoutWindowStartedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

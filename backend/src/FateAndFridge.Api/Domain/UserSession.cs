namespace FateAndFridge.Api.Domain;

/// <summary>
/// Database-backed opaque session. Only a hash of the session token is stored;
/// timestamps are set by the application and are always UTC.
/// </summary>
public sealed class UserSession
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>Hash of the session token; unique. Never the raw token.</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset IdleExpiresAt { get; set; }

    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}

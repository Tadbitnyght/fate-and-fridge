namespace FateAndFridge.Api.Domain;

/// <summary>
/// Security-relevant authentication event (for example <c>login_success</c> or
/// <c>login_failure</c>). <see cref="OccurredAt"/> is always UTC.
/// </summary>
public sealed class AuthEvent
{
    public long Id { get; set; }

    /// <summary>Nullable because failed attempts may not resolve to a user.</summary>
    public long? UserId { get; set; }

    public string? UsernameAttempted { get; set; }

    public required string EventType { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Optional JSONB payload with event-specific details.</summary>
    public string? MetadataJson { get; set; }
}

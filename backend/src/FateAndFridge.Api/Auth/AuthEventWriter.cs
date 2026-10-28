using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Stable <c>auth_events.event_type</c> values (docs/DATABASE.md §2).
/// <c>account_locked</c> is written when a lock engages; a login attempt
/// rejected by an already-active lock is recorded as <see cref="LoginFailure"/>
/// with reason <c>account_locked</c>.
/// </summary>
public static class AuthEventTypes
{
    public const string LoginSuccess = "login_success";

    public const string LoginFailure = "login_failure";

    public const string AccountLocked = "account_locked";

    public const string PasswordChange = "password_change";

    public const string PasswordReset = "password_reset";
}

/// <summary>
/// One <c>auth_events</c> row to persist (docs/SECURITY.md §17). Never carries
/// passwords, hashes, or tokens; <see cref="UsernameAttempted"/> is the
/// trimmed/normalized input, and <see cref="Metadata"/> holds only event
/// categories (for example <c>{ "reason": "invalid_password" }</c>).
/// </summary>
public sealed record AuthEventEntry(
    string EventType,
    long? UserId,
    string? UsernameAttempted,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Writes security-relevant authentication events.</summary>
public interface IAuthEventWriter
{
    Task WriteAsync(AuthEventEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists <see cref="AuthEvent"/> rows stamped with the injected UTC clock.
/// <see cref="AuthEventEntry.Metadata"/> is serialized verbatim to
/// <c>metadata_json</c>; callers must keep secrets out of it.
/// </summary>
public sealed class AuthEventWriter(AppDbContext dbContext, TimeProvider timeProvider) : IAuthEventWriter
{
    public async Task WriteAsync(AuthEventEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        dbContext.AuthEvents.Add(new AuthEvent
        {
            UserId = entry.UserId,
            UsernameAttempted = entry.UsernameAttempted,
            EventType = entry.EventType,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            OccurredAt = timeProvider.GetUtcNow(),
            MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

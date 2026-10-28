using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>Outcome of a household auth-mode switch (docs/API.md §21).</summary>
public abstract record AuthModeSwitchResult
{
    private AuthModeSwitchResult()
    {
    }

    /// <summary>
    /// The household switched to multiuser mode and the first Admin is signed
    /// in; the endpoint applies the cookie/CSRF pair from the session.
    /// </summary>
    public sealed record Success(User Admin, UserSession Session, string RawSessionToken) : AuthModeSwitchResult;

    /// <summary>The household already uses multiuser mode.</summary>
    public sealed record AlreadyMultiuser : AuthModeSwitchResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : AuthModeSwitchResult;

    /// <summary>The household settings row is missing (setup not complete).</summary>
    public sealed record NotConfigured : AuthModeSwitchResult;
}

/// <summary>
/// Single-user → multiuser conversion (docs/API.md §21). Creates the
/// first Admin and flips <c>household_settings.auth_mode</c> atomically under
/// the admin-safety advisory lock, so concurrent switches create exactly one
/// Admin. Multiuser → single-user stays unsupported.
/// </summary>
public sealed class AuthModeService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    SessionService sessionService,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<AuthModeService> logger)
{
    /// <summary>Stable audit entity id for the auth-mode setting.</summary>
    public const string AuthModeEntityId = "auth_mode";

    public async Task<AuthModeSwitchResult> SwitchToMultiuserAsync(
        string username,
        string normalizedUsername,
        string password,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({UserManagementLock.AdminSafetyKey})",
            cancellationToken);

        var settings = await dbContext.HouseholdSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return new AuthModeSwitchResult.NotConfigured();
        }

        if (settings.AuthMode == AuthMode.Multiuser)
        {
            return new AuthModeSwitchResult.AlreadyMultiuser();
        }

        if (await dbContext.Users.AnyAsync(
                candidate => candidate.NormalizedUsername == normalizedUsername,
                cancellationToken))
        {
            return new AuthModeSwitchResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["admin.username"] = ["admin.username is already taken."],
                });
        }

        var now = timeProvider.GetUtcNow();
        var admin = new User
        {
            Username = username,
            NormalizedUsername = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = false,
            CreatedAt = now,
            UpdatedAt = now,
            LastLoginAt = now,
        };
        dbContext.Users.Add(admin);

        settings.AuthMode = AuthMode.Multiuser;
        settings.UpdatedAt = now;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a race against another creator of the same username.
            return new AuthModeSwitchResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["admin.username"] = ["admin.username is already taken."],
                });
        }

        var (session, rawSessionToken) = await sessionService.CreateAsync(
            admin.Id,
            ipAddress,
            userAgent,
            cancellationToken);

        // The settings switch and its audit row commit with the new Admin; the
        // credential (password/hash) is never part of the payload. The caller is
        // the single-user system actor, so actor_user_id stays null
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.SettingsAuthModeSwitched,
                AuditEntityTypes.Settings,
                EntityId: AuthModeEntityId,
                BeforeJson: """{"auth_mode":"single_user"}""",
                AfterJson: """{"auth_mode":"multiuser"}""",
                IpAddress: ipAddress,
                Metadata: new Dictionary<string, object?> { ["admin_user_id"] = admin.Id }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Household switched from single-user to multiuser mode");

        return new AuthModeSwitchResult.Success(admin, session, rawSessionToken);
    }
}

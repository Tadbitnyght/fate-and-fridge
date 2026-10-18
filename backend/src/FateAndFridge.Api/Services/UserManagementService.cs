using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.LiveSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>Public user record (docs/API.md §6/§21).</summary>
public sealed record UserRecord(
    long Id,
    string Username,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Permissions);

/// <summary>Outcome of a user-management mutation (M1-10).</summary>
public abstract record UserMutationResult
{
    private UserMutationResult()
    {
    }

    /// <summary>
    /// The mutation committed. <see cref="TemporaryPassword"/> is set exactly
    /// once when the account was created without an explicit password.
    /// </summary>
    public sealed record Success(
        UserRecord User,
        string? TemporaryPassword = null,
        bool SessionsRevoked = false) : UserMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : UserMutationResult;

    public sealed record NotFound : UserMutationResult;

    /// <summary>The mutation would remove the last active Admin (docs/API.md §21).</summary>
    public sealed record LastAdminProtected : UserMutationResult;
}

/// <summary>
/// User management (M1-10, docs/API.md §6/§21): listing, creation, detail,
/// rename/role changes, permission replacement, and enable/disable with
/// last-active-Admin safety. Role and enable/disable mutations run in a
/// transaction holding an advisory lock so two concurrent mutations can never
/// leave the household without an active Admin. Admin-created accounts always
/// start with <c>must_change_password = true</c>.
/// </summary>
public sealed class UserManagementService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    SessionService sessionService,
    ILiveSyncPublisher liveSync,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<UserManagementService> logger)
{
    /// <summary><c>user_sessions.revoked_reason</c> for a disabled user.</summary>
    public const string DisabledRevocationReason = "user_disabled";

    /// <summary><c>user_sessions.revoked_reason</c> for a role change.</summary>
    public const string RoleChangedRevocationReason = "role_changed";

    private static readonly string UsernameMessage =
        $"username must be between {UsernamePolicy.MinLength} and {UsernamePolicy.MaxLength} characters using letters, digits, '.', '_' or '-'.";

    /// <summary>Lists all users with their effective permissions.</summary>
    public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.Username)
            .ToListAsync(cancellationToken);

        var grants = await dbContext.UserPermissions
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var grantsByUser = grants
            .GroupBy(grant => grant.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(grant => grant.PermissionKey).ToList());

        return users
            .Select(user => ToRecord(
                user,
                EffectiveKeys(user, grantsByUser.GetValueOrDefault(user.Id) ?? [])))
            .ToList();
    }

    /// <summary>Returns one user record, or null when the id is unknown.</summary>
    public async Task<UserRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var grantedKeys = await dbContext.UserPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);

        return ToRecord(user, EffectiveKeys(user, grantedKeys));
    }

    /// <summary>
    /// Creates a user. Without an explicit password a one-time temporary
    /// password is generated and returned once; every admin-created account
    /// starts with <c>must_change_password = true</c>. The account, its default
    /// grants, and the <c>user.created</c> audit row commit atomically; the
    /// password and temporary password are never audited.
    /// </summary>
    public async Task<UserMutationResult> CreateAsync(
        string? username,
        string? roleText,
        string? password,
        bool passwordProvided,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? trimmedUsername = null;

        if (username is null)
        {
            errors["username"] = ["username is required and must be a string."];
        }
        else
        {
            trimmedUsername = username.Trim();
            if (!UsernamePolicy.IsValid(trimmedUsername))
            {
                errors["username"] = [UsernameMessage];
            }
        }

        var role = UserRole.Member;
        if (roleText is not null && !EnumTextConverters.TryUserRoleFromText(roleText, out role))
        {
            errors["role"] = ["role must be \"admin\" or \"member\"."];
        }

        string? validatedPassword = null;
        if (passwordProvided)
        {
            if (!PasswordPolicy.IsValid(password))
            {
                errors["password"] = [
                    $"password must be between {PasswordPolicy.MinLength} and {PasswordPolicy.MaxLength} characters."];
            }
            else
            {
                validatedPassword = password;
            }
        }

        if (errors.Count > 0)
        {
            return new UserMutationResult.ValidationFailed(errors);
        }

        var normalizedUsername = UsernameNormalizer.Normalize(trimmedUsername!);
        if (await dbContext.Users.AnyAsync(
                candidate => candidate.NormalizedUsername == normalizedUsername,
                cancellationToken))
        {
            return ValidationFailed("username", "username is already taken.");
        }

        var temporaryPassword = validatedPassword is null
            ? TemporaryPasswordGenerator.Generate()
            : null;
        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            Username = trimmedUsername!,
            NormalizedUsername = normalizedUsername,
            PasswordHash = passwordHasher.Hash(validatedPassword ?? temporaryPassword!),
            Role = role,
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Users.Add(user);

        var grantedKeys = role == UserRole.Member ? PermissionKeys.MemberViewOnly : [];

        // Account, default grants, and the audit row commit atomically
        // (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a create race on normalized_username.
            dbContext.Entry(user).State = EntityState.Detached;
            return ValidationFailed("username", "username is already taken.");
        }

        if (role == UserRole.Member)
        {
            // Approved M1 default: new members are view-only (docs/API.md §6).
            foreach (var permissionKey in PermissionKeys.MemberViewOnly)
            {
                dbContext.UserPermissions.Add(new UserPermission
                {
                    UserId = user.Id,
                    PermissionKey = permissionKey,
                });
            }
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.UserCreated,
                AuditEntityTypes.User,
                EntityId: user.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(user, grantedKeys),
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: temporaryPassword is null
                    ? null
                    : new Dictionary<string, object?> { ["temporary_password_issued"] = true }),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created user {UserId} with role {Role}", user.Id, EnumTextConverters.UserRoleToText(role));
        return new UserMutationResult.Success(
            ToRecord(user, EffectiveKeys(user, grantedKeys)),
            temporaryPassword);
    }

    /// <summary>
    /// Renames a user and/or changes their role. Demoting the last active Admin
    /// is rejected; a role change revokes the target's sessions so the new role
    /// takes effect on the next login. Exactly one audit row is staged
    /// (<c>user.role_changed</c> takes precedence over <c>user.renamed</c> when
    /// both change in one request).
    /// </summary>
    public async Task<UserMutationResult> UpdateAsync(
        long id,
        string? username,
        bool renameRequested,
        string? roleText,
        bool roleRequested,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? trimmedUsername = null;

        if (renameRequested)
        {
            if (username is null)
            {
                errors["username"] = ["username must be a string."];
            }
            else
            {
                trimmedUsername = username.Trim();
                if (!UsernamePolicy.IsValid(trimmedUsername))
                {
                    errors["username"] = [UsernameMessage];
                }
            }
        }

        UserRole? newRole = null;
        if (roleRequested)
        {
            if (roleText is null || !EnumTextConverters.TryUserRoleFromText(roleText, out var parsedRole))
            {
                errors["role"] = ["role must be \"admin\" or \"member\"."];
            }
            else
            {
                newRole = parsedRole;
            }
        }

        if (errors.Count > 0)
        {
            return new UserMutationResult.ValidationFailed(errors);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AcquireAdminLockAsync(cancellationToken);

        var user = await dbContext.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
        {
            return new UserMutationResult.NotFound();
        }

        var grantedKeys = await dbContext.UserPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);
        var beforeJson = SnapshotJson(user, grantedKeys);
        var previousUsername = user.Username;

        if (trimmedUsername is not null)
        {
            var normalizedUsername = UsernameNormalizer.Normalize(trimmedUsername);
            if (!string.Equals(normalizedUsername, user.NormalizedUsername, StringComparison.Ordinal)
                && await dbContext.Users.AnyAsync(
                    candidate => candidate.NormalizedUsername == normalizedUsername && candidate.Id != id,
                    cancellationToken))
            {
                return ValidationFailed("username", "username is already taken.");
            }

            user.Username = trimmedUsername;
            user.NormalizedUsername = normalizedUsername;
        }

        var roleChanged = false;
        var seededDefaultGrants = false;
        if (newRole is { } role && role != user.Role)
        {
            if (user.Role == UserRole.Admin
                && user.IsActive
                && role == UserRole.Member
                && await CountOtherActiveAdminsAsync(id, cancellationToken) == 0)
            {
                return new UserMutationResult.LastAdminProtected();
            }

            if (role == UserRole.Member
                && !await dbContext.UserPermissions.AnyAsync(
                    permission => permission.UserId == id,
                    cancellationToken))
            {
                // Admins rely on their role for all seven keys, so a demotion
                // would otherwise leave the user with zero visibility. Seed the
                // approved view-only defaults; existing grants are preserved.
                foreach (var permissionKey in PermissionKeys.MemberViewOnly)
                {
                    dbContext.UserPermissions.Add(new UserPermission
                    {
                        UserId = id,
                        PermissionKey = permissionKey,
                    });
                }

                seededDefaultGrants = true;
            }

            user.Role = role;
            roleChanged = true;
        }

        user.UpdatedAt = timeProvider.GetUtcNow();

        // One row per write; the granted-key sets record any demotion seeding.
        var afterKeys = seededDefaultGrants
            ? grantedKeys.Concat(PermissionKeys.MemberViewOnly)
            : grantedKeys;
        var renamed = !string.Equals(user.Username, previousUsername, StringComparison.Ordinal);
        await auditService.WriteAsync(
            new AuditEventEntry(
                roleChanged ? AuditEventTypes.UserRoleChanged : AuditEventTypes.UserRenamed,
                AuditEntityTypes.User,
                EntityId: id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(user, afterKeys),
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: new Dictionary<string, object?>
                {
                    ["renamed"] = renamed,
                    ["role_changed"] = roleChanged,
                }),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a rename race on normalized_username.
            return ValidationFailed("username", "username is already taken.");
        }

        if (roleChanged)
        {
            // Privilege change: drop existing sessions so the stale role claim
            // cannot outlive the new role.
            await sessionService.RevokeAllForUserAsync(id, RoleChangedRevocationReason, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (roleChanged)
        {
            // Only after the commit: notify the revoked sessions' live-sync
            // connections and abort them (docs/M9_PLAN.md §1.13).
            await liveSync.PublishSessionRevokedForUserAsync(id, exceptSessionId: null, cancellationToken);
        }

        return new UserMutationResult.Success(
            await GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("User disappeared."),
            SessionsRevoked: roleChanged);
    }

    /// <summary>
    /// Replaces a member's granted permission rows. Unknown keys are rejected;
    /// Admins always hold all keys and cannot have rows set. Grants are read
    /// per request, so the change takes effect immediately. The before/after
    /// key sets are audited; no other user field is included.
    /// </summary>
    public async Task<UserMutationResult> ReplacePermissionsAsync(
        long id,
        IReadOnlyList<string> permissions,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var unknownKeys = permissions
            .Where(key => !PermissionKeys.IsKnown(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownKeys.Length > 0)
        {
            return ValidationFailed(
                "permissions",
                $"Unknown permission keys: {string.Join(", ", unknownKeys)}.");
        }

        var user = await dbContext.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
        {
            return new UserMutationResult.NotFound();
        }

        if (user.Role == UserRole.Admin)
        {
            return ValidationFailed(
                "permissions",
                "Admin users always hold all permissions; permission rows cannot be set.");
        }

        var normalizedKeys = PermissionKeys.All
            .Where(key => permissions.Contains(key, StringComparer.Ordinal))
            .ToArray();

        var existing = await dbContext.UserPermissions
            .Where(permission => permission.UserId == id)
            .ToListAsync(cancellationToken);
        var beforeJson = SnapshotJson(user, existing.Select(permission => permission.PermissionKey));

        // Grant replacement and its audit row commit atomically.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.UserPermissions.RemoveRange(existing);
        foreach (var key in normalizedKeys)
        {
            dbContext.UserPermissions.Add(new UserPermission
            {
                UserId = id,
                PermissionKey = key,
            });
        }

        user.UpdatedAt = timeProvider.GetUtcNow();
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.UserPermissionsChanged,
                AuditEntityTypes.User,
                EntityId: id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(user, normalizedKeys),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new UserMutationResult.Success(
            await GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("User disappeared."));
    }

    /// <summary>
    /// Disables or enables a user. Disabling revokes all sessions and is
    /// rejected for the last active Admin; enabling clears every lockout
    /// counter so the account can log in again (docs/API.md §6).
    /// </summary>
    public async Task<UserMutationResult> SetActiveAsync(
        long id,
        bool activate,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AcquireAdminLockAsync(cancellationToken);

        var user = await dbContext.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
        {
            return new UserMutationResult.NotFound();
        }

        if (!activate && !user.IsActive)
        {
            // Disabling an already-disabled user is a no-op.
            await transaction.CommitAsync(cancellationToken);
            return new UserMutationResult.Success(
                await GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("User disappeared."));
        }

        if (!activate
            && user.Role == UserRole.Admin
            && await CountOtherActiveAdminsAsync(id, cancellationToken) == 0)
        {
            return new UserMutationResult.LastAdminProtected();
        }

        var grantedKeys = await dbContext.UserPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);
        var beforeJson = SnapshotJson(user, grantedKeys);

        user.IsActive = activate;
        user.UpdatedAt = timeProvider.GetUtcNow();

        if (activate)
        {
            user.FailedLoginCount = 0;
            user.LastFailedLoginAt = null;
            user.LockedUntil = null;
            user.LockoutCount = 0;
            user.LockoutWindowStartedAt = null;
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                activate ? AuditEventTypes.UserEnabled : AuditEventTypes.UserDisabled,
                AuditEntityTypes.User,
                EntityId: id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(user, grantedKeys),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!activate)
        {
            await sessionService.RevokeAllForUserAsync(id, DisabledRevocationReason, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (!activate)
        {
            // Only after the commit: notify the revoked sessions' live-sync
            // connections and abort them (docs/M9_PLAN.md §1.13).
            await liveSync.PublishSessionRevokedForUserAsync(id, exceptSessionId: null, cancellationToken);
        }

        logger.LogInformation(
            "{Action} user {UserId}",
            activate ? "Enabled" : "Disabled",
            id);

        return new UserMutationResult.Success(
            await GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("User disappeared."),
            SessionsRevoked: !activate);
    }

    private Task AcquireAdminLockAsync(CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({UserManagementLock.AdminSafetyKey})",
            cancellationToken);

    private Task<int> CountOtherActiveAdminsAsync(long exceptUserId, CancellationToken cancellationToken) =>
        dbContext.Users
            .AsNoTracking()
            .CountAsync(
                candidate => candidate.IsActive
                    && candidate.Role == UserRole.Admin
                    && candidate.Id != exceptUserId,
                cancellationToken);

    /// <summary>
    /// Secret-free user snapshot for <c>before_json</c>/<c>after_json</c>: the
    /// account's public fields plus the sorted granted permission keys. The
    /// password hash, any plaintext or temporary password, session tokens, and
    /// lockout internals are deliberately excluded (docs/SECURITY.md §17).
    /// </summary>
    private static string SnapshotJson(User user, IEnumerable<string> grantedKeys) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = user.Id,
            ["username"] = user.Username,
            ["role"] = EnumTextConverters.UserRoleToText(user.Role),
            ["is_active"] = user.IsActive,
            ["must_change_password"] = user.MustChangePassword,
            ["permissions"] = grantedKeys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray(),
        });

    private static IReadOnlyList<string> EffectiveKeys(User user, IEnumerable<string> grantedKeys) =>
        PermissionEvaluator.For(
            new CurrentActor(IsSystem: false, Role: user.Role) { UserId = user.Id },
            grantedKeys);

    private static UserRecord ToRecord(User user, IReadOnlyList<string> permissions) =>
        new(
            user.Id,
            user.Username,
            EnumTextConverters.UserRoleToText(user.Role),
            user.IsActive,
            user.MustChangePassword,
            user.LastLoginAt,
            permissions);

    private static UserMutationResult ValidationFailed(string field, string message) =>
        new UserMutationResult.ValidationFailed(
            new Dictionary<string, string[]> { [field] = [message] });
}

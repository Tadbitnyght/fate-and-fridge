using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>Outcome of a setup completion attempt (docs/API.md §21).</summary>
public abstract record SetupCompletionResult
{
    private SetupCompletionResult()
    {
    }

    /// <summary>
    /// Setup committed. <see cref="Admin"/>/<see cref="Session"/> are set in
    /// multiuser mode; <see cref="RawSessionToken"/> is the raw cookie value.
    /// </summary>
    public sealed record Success(
        string RecoveryKey,
        AuthMode AuthMode,
        User? Admin,
        UserSession? Session,
        string? RawSessionToken) : SetupCompletionResult;

    /// <summary>Setup was already completed; nothing was written.</summary>
    public sealed record AlreadyCompleted : SetupCompletionResult;

    /// <summary>
    /// The existing recovery key file is unusable (empty, malformed, or
    /// unreadable). The database transaction was rolled back; the endpoint maps
    /// this to a stable 500 with an operator-facing message.
    /// </summary>
    public sealed record RecoveryKeyUnusable(string Message) : SetupCompletionResult;
}

/// <summary>
/// Single-shot, atomic setup completion (docs/API.md §21): household settings
/// application settings, the first Admin (multiuser), its session, and the
/// recovery key are committed as one unit. Concurrent completions are
/// serialized with a transaction-scoped advisory lock, so exactly one caller
/// succeeds and the rest observe <see cref="SetupCompletionResult.AlreadyCompleted"/>.
/// </summary>
public sealed class SetupCompletionService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    SessionService sessionService,
    RecoveryKeyStore recoveryKeyStore,
    TimeProvider timeProvider,
    ILogger<SetupCompletionService> logger)
{
    public const string UpdateCheckEnabledKey = "update_check_enabled";

    public const string IpAllowlistEnabledKey = "ip_allowlist_enabled";

    public const string IpAllowlistRangesKey = "ip_allowlist_ranges";

    /// <summary>Empty list for trusted-proxy configuration.</summary>
    public const string TrustedProxiesKey = "trusted_proxies";

    /// <summary>
    /// Advisory transaction lock key for setup completion; any fixed bigint
    /// works, this one spells "__SETUP".
    /// </summary>
    private const long SetupAdvisoryLockKey = 0x5F5F5345545550L;

    public async Task<SetupCompletionResult> CompleteAsync(
        SetupCompletionRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Serialize concurrent completions inside the transaction: the lock is
        // held until commit/rollback, so a bare pre-check cannot race.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({SetupAdvisoryLockKey})",
            cancellationToken);

        var existingSetupCompleted = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SetupStateService.SetupCompletedKey)
            .Select(setting => setting.ValueJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (SetupStateService.IsTrueJson(existingSetupCompleted))
        {
            return new SetupCompletionResult.AlreadyCompleted();
        }

        var now = timeProvider.GetUtcNow();

        var household = await dbContext.HouseholdSettings.SingleOrDefaultAsync(cancellationToken);
        if (household is null)
        {
            household = new HouseholdSettings
            {
                Id = 1,
                HouseholdName = request.HouseholdName,
                Timezone = request.Timezone,
                DefaultDinerCount = request.DefaultDinerCount,
                WeekStartsOn = request.WeekStartsOn,
                StartupPage = request.StartupPage,
                AuthMode = request.AuthMode,
                CreatedAt = now,
                UpdatedAt = now,
            };
            dbContext.HouseholdSettings.Add(household);
        }
        else
        {
            household.HouseholdName = request.HouseholdName;
            household.Timezone = request.Timezone;
            household.DefaultDinerCount = request.DefaultDinerCount;
            household.WeekStartsOn = request.WeekStartsOn;
            household.StartupPage = request.StartupPage;
            household.AuthMode = request.AuthMode;
            household.UpdatedAt = now;
        }

        await UpsertSettingAsync(
            SetupStateService.SetupCompletedKey,
            JsonSerializer.Serialize(true),
            now,
            cancellationToken);
        await UpsertSettingAsync(
            UpdateCheckEnabledKey,
            JsonSerializer.Serialize(request.UpdateCheckEnabled),
            now,
            cancellationToken);
        await UpsertSettingAsync(
            IpAllowlistEnabledKey,
            JsonSerializer.Serialize(request.IpAllowlistEnabled),
            now,
            cancellationToken);
        await UpsertSettingAsync(
            IpAllowlistRangesKey,
            JsonSerializer.Serialize(request.IpAllowlistRanges),
            now,
            cancellationToken);
        await UpsertSettingAsync(TrustedProxiesKey, "[]", now, cancellationToken);

        User? admin = null;
        UserSession? session = null;
        string? rawSessionToken = null;

        if (request.AuthMode == AuthMode.Multiuser && request.Admin is { } credentials)
        {
            admin = new User
            {
                Username = credentials.Username,
                NormalizedUsername = credentials.NormalizedUsername,
                PasswordHash = passwordHasher.Hash(credentials.Password),
                Role = UserRole.Admin,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                // The first Admin is signed in immediately, like a login.
                LastLoginAt = now,
            };
            dbContext.Users.Add(admin);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Same session establishment as a successful login.
            (session, rawSessionToken) = await sessionService.CreateAsync(
                admin.Id,
                ipAddress,
                userAgent,
                cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // The file write stays inside the transaction: a failure here rolls the
        // database back, so a retry starts clean and reuses any key already
        // written. An unusable existing file fails loudly instead of returning
        // invalid key material.
        string recoveryKey;
        try
        {
            recoveryKey = await recoveryKeyStore.GetOrCreateAsync(cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            // The store already logged the actionable operator error.
            return new SetupCompletionResult.RecoveryKeyUnusable(exception.Message);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Setup completed in {AuthMode} mode",
            EnumTextConverters.AuthModeToText(request.AuthMode));

        return new SetupCompletionResult.Success(
            recoveryKey,
            request.AuthMode,
            admin,
            session,
            rawSessionToken);
    }

    private async Task UpsertSettingAsync(
        string key,
        string valueJson,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({key}, {valueJson}::jsonb, {now})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
}

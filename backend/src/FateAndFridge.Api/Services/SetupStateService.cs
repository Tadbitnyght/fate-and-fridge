using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>Setup status payload for <c>GET /setup/status</c> (docs/API.md §21).</summary>
public sealed record SetupStatus(bool SetupCompleted, string? AuthMode, bool RecoveryKeyPresent);

/// <summary>
/// Reads and writes the single-shot setup state kept in
/// <c>application_settings</c> (docs/DATABASE.md §11) and reports whether the
/// backup recovery key file exists. Recovery key generation is a separate step.
/// </summary>
public sealed class SetupStateService(AppDbContext dbContext, IConfiguration configuration)
{
    /// <summary>Key of the JSON boolean flag in <c>application_settings.key</c>.</summary>
    public const string SetupCompletedKey = "setup_completed";

    private readonly string _recoveryKeyPath = RecoveryKeyStore.FilePathFor(configuration);

    /// <summary>Returns whether <c>setup_completed</c> is stored as JSON <c>true</c>.</summary>
    public async Task<bool> IsSetupCompletedAsync(CancellationToken cancellationToken = default)
    {
        var valueJson = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SetupCompletedKey)
            .Select(setting => setting.ValueJson)
            .SingleOrDefaultAsync(cancellationToken);

        return IsTrueJson(valueJson);
    }

    /// <summary>Returns the public setup status payload described in docs/API.md §21.</summary>
    public async Task<SetupStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var setupCompleted = await IsSetupCompletedAsync(cancellationToken);

        var authMode = await dbContext.HouseholdSettings
            .AsNoTracking()
            .Select(settings => (AuthMode?)settings.AuthMode)
            .SingleOrDefaultAsync(cancellationToken);

        return new SetupStatus(
            setupCompleted,
            authMode is null ? null : EnumTextConverters.AuthModeToText(authMode.Value),
            File.Exists(_recoveryKeyPath));
    }

    /// <summary>
    /// Idempotently stores the <c>setup_completed</c> flag and refreshes
    /// <c>updated_at</c>. Uses a single atomic PostgreSQL upsert so concurrent
    /// callers cannot create duplicate rows (docs/API.md §21).
    /// </summary>
    public async Task SetSetupCompletedAsync(
        bool completed = true,
        CancellationToken cancellationToken = default)
    {
        var valueJson = JsonSerializer.Serialize(completed);
        var updatedAt = DateTimeOffset.UtcNow;

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupCompletedKey}, {valueJson}::jsonb, {updatedAt})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    internal static bool IsTrueJson(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

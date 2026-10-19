using System.Text.Json;
using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>Stored security settings (docs/API.md §21).</summary>
public sealed record SecuritySettings(
    bool IpAllowlistEnabled,
    IReadOnlyList<string> IpAllowlistRanges,
    IReadOnlyList<string> TrustedProxies,
    int AuditRetentionDays)
{
    /// <summary>Allowlist disabled, no ranges, no trusted proxies, default retention.</summary>
    public static readonly SecuritySettings Default =
        new(false, [], [], SecuritySettingsService.DefaultAuditRetentionDays);
}

/// <summary>
/// Loads and persists the security settings from <c>application_settings</c>
/// (docs/DATABASE.md §11): <c>ip_allowlist_enabled</c>,
/// <c>ip_allowlist_ranges</c>, and <c>trusted_proxies</c>. Values are read once
/// per request and cached for a short TTL
/// (<see cref="CacheTtl"/>), with the PATCH endpoint updating the cache
/// directly, so changes take effect immediately without a database query per
/// request. A read failure logs a warning and falls back to
/// <see cref="SecuritySettings.Default"/> (fail open) so health and error paths
/// stay reachable during a database outage.
/// </summary>
public sealed class SecuritySettingsService(
    AppDbContext dbContext,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<SecuritySettingsService> logger)
{
    /// <summary>Cross-request cache key (single household).</summary>
    public const string CacheKey = "faf.security_settings";

    /// <summary>Stable audit entity id for the security settings.</summary>
    public const string SecuritySettingsEntityId = "security";

    /// <summary>
    /// Reserved <c>application_settings</c> key for the audit retention window
    /// (docs/M12_PLAN.md §1.8). <c>0</c> means "keep forever"; otherwise a value
    /// between <see cref="MinAuditRetentionDays"/> and
    /// <see cref="MaxAuditRetentionDays"/> days.
    /// </summary>
    public const string AuditRetentionDaysKey = "audit_retention_days";

    /// <summary>Retention applied when the setting is absent (one year).</summary>
    public const int DefaultAuditRetentionDays = 365;

    /// <summary>Smallest bounded retention window, exclusive of "keep forever" (0).</summary>
    public const int MinAuditRetentionDays = 7;

    /// <summary>Largest bounded retention window (ten years).</summary>
    public const int MaxAuditRetentionDays = 3650;

    /// <summary><c>0</c> is "keep forever"; otherwise the value is bounded.</summary>
    public static bool IsValidAuditRetentionDays(int value) =>
        value == 0 || (value >= MinAuditRetentionDays && value <= MaxAuditRetentionDays);

    /// <summary>Short TTL so out-of-band database changes are picked up.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private SecuritySettings? _requestScoped;

    /// <summary>Returns the stored settings (normalized), cached for a short TTL.</summary>
    public async Task<SecuritySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_requestScoped is not null)
        {
            return _requestScoped;
        }

        if (cache.TryGetValue<SecuritySettings>(CacheKey, out var cached) && cached is not null)
        {
            _requestScoped = cached;
            return cached;
        }

        try
        {
            var stored = await dbContext.ApplicationSettings
                .AsNoTracking()
                .Where(setting => setting.Key == SetupCompletionService.IpAllowlistEnabledKey
                    || setting.Key == SetupCompletionService.IpAllowlistRangesKey
                    || setting.Key == SetupCompletionService.TrustedProxiesKey
                    || setting.Key == AuditRetentionDaysKey)
                .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson, cancellationToken);

            var settings = new SecuritySettings(
                ReadBoolean(stored.GetValueOrDefault(SetupCompletionService.IpAllowlistEnabledKey)),
                ReadNormalizedRanges(stored.GetValueOrDefault(SetupCompletionService.IpAllowlistRangesKey)),
                ReadNormalizedRanges(stored.GetValueOrDefault(SetupCompletionService.TrustedProxiesKey)),
                ReadInt(
                    stored.GetValueOrDefault(AuditRetentionDaysKey),
                    DefaultAuditRetentionDays));

            SetCache(settings);
            return settings;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not read security settings; continuing with the fail-open defaults.");
            _requestScoped = SecuritySettings.Default;
            return SecuritySettings.Default;
        }
    }

    /// <summary>
    /// Persists the settings (upsert per key) and updates the cache so the
    /// change applies to the next request immediately. The upserts and the
    /// <c>settings.security_updated</c> audit row share one transaction
    /// (docs/M12_PLAN.md §2.1).
    /// </summary>
    public async Task SaveAsync(
        SecuritySettings settings,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The request-scoped/cached value is the pre-change state the endpoint
        // read before building the patch.
        var before = await GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(
            SetupCompletionService.IpAllowlistEnabledKey,
            JsonSerializer.Serialize(settings.IpAllowlistEnabled),
            now,
            cancellationToken);
        await UpsertAsync(
            SetupCompletionService.IpAllowlistRangesKey,
            JsonSerializer.Serialize(settings.IpAllowlistRanges),
            now,
            cancellationToken);
        await UpsertAsync(
            SetupCompletionService.TrustedProxiesKey,
            JsonSerializer.Serialize(settings.TrustedProxies),
            now,
            cancellationToken);
        await UpsertAsync(
            AuditRetentionDaysKey,
            JsonSerializer.Serialize(settings.AuditRetentionDays),
            now,
            cancellationToken);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.SettingsSecurityUpdated,
                AuditEntityTypes.Settings,
                EntityId: SecuritySettingsEntityId,
                BeforeJson: SnapshotJson(before),
                AfterJson: SnapshotJson(settings),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        SetCache(settings);
    }

    private static string SnapshotJson(SecuritySettings settings) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ip_allowlist_enabled"] = settings.IpAllowlistEnabled,
            ["ip_allowlist_ranges"] = settings.IpAllowlistRanges,
            ["trusted_proxies"] = settings.TrustedProxies,
            ["audit_retention_days"] = settings.AuditRetentionDays,
        });

    private static int ReadInt(string? valueJson, int fallback)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind == JsonValueKind.Number
                && document.RootElement.TryGetInt32(out var value)
                ? value
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private void SetCache(SecuritySettings settings)
    {
        _requestScoped = settings;
        cache.Set(CacheKey, settings, CacheTtl);
    }

    private async Task UpsertAsync(
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

    private static bool ReadBoolean(string? valueJson)
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

    private static IReadOnlyList<string> ReadNormalizedRanges(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ranges = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String
                    && ClientAddress.TryNormalizeRange(element.GetString(), out var cidr))
                {
                    ranges.Add(cidr);
                }
            }

            return ranges;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

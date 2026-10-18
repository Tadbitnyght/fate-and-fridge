using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>Household settings payload (docs/API.md §21).</summary>
public sealed record HouseholdSettingsResponse(
    string HouseholdName,
    string Timezone,
    int WeekStartsOn,
    int DefaultDinerCount,
    string StartupPage,
    int RandomizerRepeatLookbackDays,
    int RandomizerRepeatLookaheadDays,
    decimal RandomizerRepeatMultiplier,
    decimal RandomizerSpecialDayMultiplier,
    decimal RandomizerProteinMultiplier,
    int RandomizerProteinNeighborDistance,
    decimal RandomizerFavoriteMultiplier,
    int RandomizerSpacingNearDays,
    int RandomizerSpacingFarDays,
    int RandomizerSpacingSearchDays)
{
    /// <summary>Projects the stored row onto the public snake_case payload.</summary>
    public static HouseholdSettingsResponse From(HouseholdSettings settings) => new(
        settings.HouseholdName,
        settings.Timezone,
        settings.WeekStartsOn,
        settings.DefaultDinerCount,
        EnumTextConverters.StartupPageToText(settings.StartupPage),
        settings.RandomizerRepeatLookbackDays,
        settings.RandomizerRepeatLookaheadDays,
        settings.RandomizerRepeatMultiplier,
        settings.RandomizerSpecialDayMultiplier,
        settings.RandomizerProteinMultiplier,
        settings.RandomizerProteinNeighborDistance,
        settings.RandomizerFavoriteMultiplier,
        settings.RandomizerSpacingNearDays,
        settings.RandomizerSpacingFarDays,
        settings.RandomizerSpacingSearchDays);
}

/// <summary>
/// Reads and updates the singleton <c>household_settings</c> row
/// (docs/DATABASE.md §2). Every write refreshes <c>updated_at</c> in UTC and
/// stages one <c>settings.household_updated</c> audit row in the same
/// transaction as the change (docs/M12_PLAN.md §2.1).
/// </summary>
public sealed class HouseholdSettingsService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService)
{
    /// <summary>Returns the singleton row, or null before setup created it.</summary>
    public async Task<HouseholdSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        await dbContext.HouseholdSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Applies the validated partial update and refreshes <c>updated_at</c>.
    /// The row must exist; setup (M1-04) creates it before gating opens.
    /// </summary>
    public async Task<HouseholdSettings> ApplyPatchAsync(
        HouseholdSettingsPatch patch,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.HouseholdSettings.SingleAsync(cancellationToken);
        var beforeJson = SnapshotJson(settings);

        if (patch.HouseholdName is { } householdName)
        {
            settings.HouseholdName = householdName;
        }

        if (patch.Timezone is { } timezone
            && !string.Equals(timezone, settings.Timezone, StringComparison.Ordinal))
        {
            // Freeze the historical boundary (docs/M5_PLAN.md §1.7): the last
            // date that was historical under the old timezone must stay
            // historical so a timezone change can never make history editable.
            var previousToday = HouseholdClock.GetLocalDate(
                HouseholdClock.ResolveTimeZone(settings.Timezone),
                timeProvider.GetUtcNow());
            var frozenThrough = previousToday.AddDays(-1);
            if (settings.HistoricalBoundaryDate is not { } boundary || frozenThrough > boundary)
            {
                settings.HistoricalBoundaryDate = frozenThrough;
            }

            settings.Timezone = timezone;
        }

        if (patch.WeekStartsOn is { } weekStartsOn)
        {
            settings.WeekStartsOn = weekStartsOn;
        }

        if (patch.DefaultDinerCount is { } dinerCount)
        {
            settings.DefaultDinerCount = dinerCount;
        }

        if (patch.StartupPage is { } startupPage)
        {
            settings.StartupPage = startupPage;
        }

        if (patch.RandomizerRepeatLookbackDays is { } repeatLookbackDays)
        {
            settings.RandomizerRepeatLookbackDays = repeatLookbackDays;
        }

        if (patch.RandomizerRepeatLookaheadDays is { } repeatLookaheadDays)
        {
            settings.RandomizerRepeatLookaheadDays = repeatLookaheadDays;
        }

        if (patch.RandomizerRepeatMultiplier is { } repeatMultiplier)
        {
            settings.RandomizerRepeatMultiplier = repeatMultiplier;
        }

        if (patch.RandomizerSpecialDayMultiplier is { } specialDayMultiplier)
        {
            settings.RandomizerSpecialDayMultiplier = specialDayMultiplier;
        }

        if (patch.RandomizerProteinMultiplier is { } proteinMultiplier)
        {
            settings.RandomizerProteinMultiplier = proteinMultiplier;
        }

        if (patch.RandomizerProteinNeighborDistance is { } proteinNeighborDistance)
        {
            settings.RandomizerProteinNeighborDistance = proteinNeighborDistance;
        }

        if (patch.RandomizerFavoriteMultiplier is { } favoriteMultiplier)
        {
            settings.RandomizerFavoriteMultiplier = favoriteMultiplier;
        }

        if (patch.RandomizerSpacingNearDays is { } spacingNearDays)
        {
            settings.RandomizerSpacingNearDays = spacingNearDays;
        }

        if (patch.RandomizerSpacingFarDays is { } spacingFarDays)
        {
            settings.RandomizerSpacingFarDays = spacingFarDays;
        }

        if (patch.RandomizerSpacingSearchDays is { } spacingSearchDays)
        {
            settings.RandomizerSpacingSearchDays = spacingSearchDays;
        }

        settings.UpdatedAt = timeProvider.GetUtcNow();

        // The change and its audit row commit atomically (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.SettingsHouseholdUpdated,
                AuditEntityTypes.Settings,
                EntityId: HouseholdSettingsEntityId,
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(settings),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return settings;
    }

    /// <summary>Stable audit entity id for the singleton household settings row.</summary>
    public const string HouseholdSettingsEntityId = "household";

    /// <summary>Secret-free household settings snapshot for audit before/after.</summary>
    private static string SnapshotJson(HouseholdSettings settings) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["household_name"] = settings.HouseholdName,
            ["timezone"] = settings.Timezone,
            ["week_starts_on"] = settings.WeekStartsOn,
            ["default_diner_count"] = settings.DefaultDinerCount,
            ["startup_page"] = EnumTextConverters.StartupPageToText(settings.StartupPage),
            ["historical_boundary_date"] = settings.HistoricalBoundaryDate?.ToString("yyyy-MM-dd"),
            ["randomizer_repeat_lookback_days"] = settings.RandomizerRepeatLookbackDays,
            ["randomizer_repeat_lookahead_days"] = settings.RandomizerRepeatLookaheadDays,
            ["randomizer_repeat_multiplier"] = settings.RandomizerRepeatMultiplier,
            ["randomizer_special_day_multiplier"] = settings.RandomizerSpecialDayMultiplier,
            ["randomizer_protein_multiplier"] = settings.RandomizerProteinMultiplier,
            ["randomizer_protein_neighbor_distance"] = settings.RandomizerProteinNeighborDistance,
            ["randomizer_favorite_multiplier"] = settings.RandomizerFavoriteMultiplier,
            ["randomizer_spacing_near_days"] = settings.RandomizerSpacingNearDays,
            ["randomizer_spacing_far_days"] = settings.RandomizerSpacingFarDays,
            ["randomizer_spacing_search_days"] = settings.RandomizerSpacingSearchDays,
        });
}

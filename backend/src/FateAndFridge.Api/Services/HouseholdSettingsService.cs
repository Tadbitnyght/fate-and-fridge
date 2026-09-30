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
    string StartupPage)
{
    /// <summary>Projects the stored row onto the public snake_case payload.</summary>
    public static HouseholdSettingsResponse From(HouseholdSettings settings) => new(
        settings.HouseholdName,
        settings.Timezone,
        settings.WeekStartsOn,
        settings.DefaultDinerCount,
        EnumTextConverters.StartupPageToText(settings.StartupPage));
}

/// <summary>
/// Reads and updates the singleton <c>household_settings</c> row
/// (docs/DATABASE.md §2). Every write refreshes <c>updated_at</c> in UTC.
/// </summary>
public sealed class HouseholdSettingsService(AppDbContext dbContext)
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
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.HouseholdSettings.SingleAsync(cancellationToken);

        if (patch.HouseholdName is { } householdName)
        {
            settings.HouseholdName = householdName;
        }

        if (patch.Timezone is { } timezone)
        {
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

        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }
}

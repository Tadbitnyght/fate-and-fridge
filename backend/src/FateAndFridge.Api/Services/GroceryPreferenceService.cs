using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// One grocery preference snapshot: the
/// per-user <c>hide_checked</c> toggle and the remembered range. A null range
/// preset means "no remembered range" and falls back to the Next 7 Days default.
/// <see cref="UpdatedAt"/> is null when no row is stored.
/// </summary>
public sealed record GroceryPreferenceSnapshot(
    bool HideChecked,
    string? RangePreset,
    DateOnly? RangeStart,
    int? RangeDays,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>The defaults returned when no stored preference exists.</summary>
    public static readonly GroceryPreferenceSnapshot Default = new(false, null, null, null);
}

/// <summary>Outcome of a grocery preference PATCH.</summary>
public abstract record GroceryPreferenceOutcome
{
    private GroceryPreferenceOutcome()
    {
    }

    /// <summary>The stored preference after the patch.</summary>
    public sealed record Success(GroceryPreferenceSnapshot Preference) : GroceryPreferenceOutcome;

    /// <summary>The patch failed cross-field/range validation.</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : GroceryPreferenceOutcome;
}

/// <summary>
/// Reads and writes the caller's grocery preferences. Preferences are per-user; the multiuser path stores them in
/// <c>user_grocery_preferences</c> keyed by <c>user_id</c>. The single-user
/// system actor has <see cref="Auth.CurrentActor.UserId"/> = null and no
/// <c>users</c> row (so the FK-bearing table cannot hold its preference), so its
/// four fields are persisted as JSON under <see cref="SystemPreferenceKey"/> in
/// the existing <c>application_settings</c> key/value table — no schema change.
/// Reads never write, so <c>GET /groceries</c> stays side-effect-free.
/// </summary>
public sealed class GroceryPreferenceService(
    AppDbContext dbContext,
    HouseholdClock clock,
    TimeProvider timeProvider)
{
    /// <summary>Reserved <c>application_settings</c> key for the system actor's preference.</summary>
    public const string SystemPreferenceKey = "system_grocery_preferences";

    /// <summary>Reads the stored preference, or the defaults when no row exists.</summary>
    public async Task<GroceryPreferenceSnapshot> GetAsync(
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId is { } userId)
        {
            var row = await dbContext.UserGroceryPreferences
                .AsNoTracking()
                .SingleOrDefaultAsync(preference => preference.UserId == userId, cancellationToken)
                .ConfigureAwait(false);

            return row is null
                ? GroceryPreferenceSnapshot.Default
                : new GroceryPreferenceSnapshot(
                    row.HideChecked,
                    row.RangePreset,
                    row.RangeStart,
                    row.RangeDays,
                    row.UpdatedAt);
        }

        var valueJson = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SystemPreferenceKey)
            .Select(setting => setting.ValueJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return ReadSystem(valueJson);
    }

    /// <summary>
    /// Applies a partial preferences update and persists the normalized result
    /// A fixed preset stores only the preset and clears
    /// the range; <c>custom</c> stores the preset plus start/days; clearing the
    /// preset stores all three as null. Range values are validated against the
    /// household today and the 366-day cap.
    /// </summary>
    public async Task<GroceryPreferenceOutcome> PatchAsync(
        long? actorUserId,
        GroceryPreferencePatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var stored = await GetAsync(actorUserId, cancellationToken).ConfigureAwait(false);
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var hideChecked = patch.HideCheckedProvided ? patch.HideChecked : stored.HideChecked;

        // A non-null range_start/range_days only belongs to a custom range; an
        // explicit null always means "clear" and is accepted (docs/API.md
        // "Preferences"), so a client may echo the GET body verbatim.
        var nonNullRangeProvided =
            (patch.StartProvided && patch.RangeStart is not null)
            || (patch.DaysProvided && patch.RangeDays is not null);

        string? finalPreset;
        DateOnly? finalStart;
        int? finalDays;

        // A patch that does not touch the range (for example a hide_checked
        // toggle) leaves the stored range exactly as it is, so a stale custom
        // range can never block an unrelated update. Only a range-touching patch
        // is normalized and validated.
        if (!patch.PresetProvided && !patch.StartProvided && !patch.DaysProvided)
        {
            finalPreset = stored.RangePreset;
            finalStart = stored.RangeStart;
            finalDays = stored.RangeDays;
        }
        else if (patch.PresetProvided && patch.RangePreset is null)
        {
            // Explicit null clears the remembered range entirely; a non-null
            // range value alongside an explicit clear is contradictory.
            if (nonNullRangeProvided)
            {
                errors["range_preset"] =
                    ["range_start and range_days are only valid with range_preset=custom."];
            }

            finalPreset = null;
            finalStart = null;
            finalDays = null;
        }
        else
        {
            // Unknown stored presets are treated as "no remembered range" so a
            // legacy value can never block an explicit range update.
            var effectivePreset = patch.PresetProvided
                ? patch.RangePreset
                : NormalizeStoredPreset(stored.RangePreset);
            var effectiveStart = patch.StartProvided ? patch.RangeStart : stored.RangeStart;
            var effectiveDays = patch.DaysProvided ? patch.RangeDays : stored.RangeDays;

            if (string.Equals(effectivePreset, GroceryListService.PresetCustom, StringComparison.Ordinal))
            {
                ValidateCustomRange(effectiveStart, effectiveDays, context.Today, errors);
                finalPreset = GroceryListService.PresetCustom;
                finalStart = effectiveStart;
                finalDays = effectiveDays;
            }
            else if (effectivePreset is null)
            {
                if (nonNullRangeProvided)
                {
                    errors["range_preset"] =
                        ["range_preset must be custom when range_start or range_days is provided."];
                }

                finalPreset = null;
                finalStart = null;
                finalDays = null;
            }
            else
            {
                // A fixed preset never carries a stored range.
                if (nonNullRangeProvided)
                {
                    errors["range_preset"] =
                        ["range_start and range_days are only valid with range_preset=custom."];
                }

                finalPreset = effectivePreset;
                finalStart = null;
                finalDays = null;
            }
        }

        if (errors.Count > 0)
        {
            return new GroceryPreferenceOutcome.ValidationFailed(errors);
        }

        var saved = await SaveAsync(
            actorUserId,
            new GroceryPreferenceSnapshot(hideChecked, finalPreset, finalStart, finalDays),
            cancellationToken).ConfigureAwait(false);

        return new GroceryPreferenceOutcome.Success(saved);
    }

    private static void ValidateCustomRange(
        DateOnly? start,
        int? days,
        DateOnly today,
        Dictionary<string, string[]> errors)
    {
        if (start is not { } startDate)
        {
            errors["range_start"] = ["range_start is required for a custom range."];
        }
        else if (startDate < today)
        {
            errors["range_start"] = ["range_start must be today or later."];
        }

        if (days is not { } dayCount)
        {
            errors["range_days"] = ["range_days is required for a custom range."];
        }
        else if (dayCount < 1 || dayCount > GroceryListService.MaxRangeDays)
        {
            errors["range_days"] =
                [$"range_days must be between 1 and {GroceryListService.MaxRangeDays}."];
        }
    }

    private async Task<GroceryPreferenceSnapshot> SaveAsync(
        long? actorUserId,
        GroceryPreferenceSnapshot preference,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (actorUserId is { } userId)
        {
            var row = await dbContext.UserGroceryPreferences
                .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                row = new UserGroceryPreference { UserId = userId };
                dbContext.UserGroceryPreferences.Add(row);
            }

            row.HideChecked = preference.HideChecked;
            row.RangePreset = preference.RangePreset;
            row.RangeStart = preference.RangeStart;
            row.RangeDays = preference.RangeDays;
            row.UpdatedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return preference with { UpdatedAt = now };
        }

        await UpsertSystemAsync(WriteSystem(preference), now, cancellationToken).ConfigureAwait(false);
        return preference with { UpdatedAt = now };
    }

    private async Task UpsertSystemAsync(
        string valueJson,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SystemPreferenceKey}, {valueJson}::jsonb, {now})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

    private static string? NormalizeStoredPreset(string? preset) =>
        preset is not null && GroceryPreferencePayloadValidator.KnownPresets.Contains(preset, StringComparer.Ordinal)
            ? preset
            : null;

    private static GroceryPreferenceSnapshot ReadSystem(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return GroceryPreferenceSnapshot.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return GroceryPreferenceSnapshot.Default;
            }

            var hideChecked = root.TryGetProperty("hide_checked", out var hideCheckedElement)
                && hideCheckedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && hideCheckedElement.GetBoolean();

            var preset = root.TryGetProperty("range_preset", out var presetElement)
                && presetElement.ValueKind == JsonValueKind.String
                    ? presetElement.GetString()
                    : null;

            DateOnly? start = null;
            if (root.TryGetProperty("range_start", out var startElement)
                && startElement.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    startElement.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStart))
            {
                start = parsedStart;
            }

            int? days = null;
            if (root.TryGetProperty("range_days", out var daysElement)
                && daysElement.ValueKind == JsonValueKind.Number
                && daysElement.TryGetInt32(out var parsedDays))
            {
                days = parsedDays;
            }

            return new GroceryPreferenceSnapshot(hideChecked, preset, start, days);
        }
        catch (JsonException)
        {
            return GroceryPreferenceSnapshot.Default;
        }
    }

    private static string WriteSystem(GroceryPreferenceSnapshot preference) =>
        JsonSerializer.Serialize(new
        {
            hide_checked = preference.HideChecked,
            range_preset = preference.RangePreset,
            range_start = preference.RangeStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range_days = preference.RangeDays,
        });
}

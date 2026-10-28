using System.Collections.ObjectModel;
using System.Text.Json;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Validated partial update of the singleton household settings row
/// (docs/API.md §21). A null property was absent from the request body and
/// must not be changed.
/// </summary>
public sealed record HouseholdSettingsPatch(
    string? HouseholdName,
    string? Timezone,
    int? WeekStartsOn,
    int? DefaultDinerCount,
    StartupPage? StartupPage,
    int? RandomizerRepeatLookbackDays = null,
    int? RandomizerRepeatLookaheadDays = null,
    decimal? RandomizerRepeatMultiplier = null,
    decimal? RandomizerSpecialDayMultiplier = null,
    decimal? RandomizerProteinMultiplier = null,
    int? RandomizerProteinNeighborDistance = null,
    decimal? RandomizerFavoriteMultiplier = null,
    int? RandomizerSpacingNearDays = null,
    int? RandomizerSpacingFarDays = null,
    int? RandomizerSpacingSearchDays = null);

/// <summary>
/// Outcome of validating a household settings PATCH body. <see cref="Patch"/>
/// is null when any field failed validation.
/// </summary>
public sealed record HouseholdSettingsPatchValidation(
    HouseholdSettingsPatch? Patch,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Validates a partial household settings PATCH body (docs/API.md §21).
/// Unknown JSON fields are ignored; a body without any known field is treated
/// as an empty patch and rejected. At most one error is reported per field.
/// </summary>
public static class HouseholdSettingsPatchValidator
{
    public const int MaxHouseholdNameLength = 100;
    public const int MinDinerCount = 1;
    public const int MaxDinerCount = 20;
    public const int MinWeekStartsOn = 0;
    public const int MaxWeekStartsOn = 6;

    public const int MinRandomizerRepeatLookbackDays = 0;
    public const int MaxRandomizerRepeatLookbackDays = 60;
    public const int MinRandomizerRepeatLookaheadDays = 0;
    public const int MaxRandomizerRepeatLookaheadDays = 60;
    public const decimal MinRandomizerRepeatMultiplier = 0.01m;
    public const decimal MaxRandomizerRepeatMultiplier = 1.00m;
    public const decimal MinRandomizerSpecialDayMultiplier = 0.00m;
    public const decimal MaxRandomizerSpecialDayMultiplier = 1.00m;
    public const decimal MinRandomizerProteinMultiplier = 0.10m;
    public const decimal MaxRandomizerProteinMultiplier = 1.00m;
    public const int MinRandomizerProteinNeighborDistance = 1;
    public const int MaxRandomizerProteinNeighborDistance = 7;
    public const decimal MinRandomizerFavoriteMultiplier = 1.00m;
    public const decimal MaxRandomizerFavoriteMultiplier = 2.00m;
    public const int MinRandomizerSpacingNearDays = 1;
    public const int MaxRandomizerSpacingNearDays = 30;
    public const int MinRandomizerSpacingFarDays = 1;
    public const int MaxRandomizerSpacingFarDays = 60;
    public const int MinRandomizerSpacingSearchDays = 7;
    public const int MaxRandomizerSpacingSearchDays = 365;

    /// <summary>
    /// Names under the tzdata directory that resolve as time zones but are not
    /// IANA identifiers (helpers or symlinks), so they must not be accepted.
    /// </summary>
    private static readonly string[] NonZoneFileNames = ["posixrules", "localtime", "Factory"];

    /// <summary>
    /// Canonical IANA timezone IDs from the host tzdb, keyed case-insensitively
    /// and built once per process. <see cref="TimeZoneInfo.GetSystemTimeZones"/>
    /// supplies the platform zone list (zone.tab on Linux); the tzdata directory
    /// adds valid zones missing from that list, such as <c>Etc/GMT+5</c>.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CanonicalTimeZoneIds =
        new(BuildCanonicalTimeZoneIds, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>());

    /// <summary>Validates the body and returns either a patch or field errors.</summary>
    public static HouseholdSettingsPatchValidation Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body", "The request body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Unknown properties are ignored. When a key appears more than once
        // JsonElement.TryGetProperty returns the last occurrence (last-one-wins)
        // no extra de-duplication is applied here.
        var hasHouseholdName = body.TryGetProperty("household_name", out var householdName);
        var hasTimezone = body.TryGetProperty("timezone", out var timezone);
        var hasWeekStartsOn = body.TryGetProperty("week_starts_on", out var weekStartsOn);
        var hasDinerCount = body.TryGetProperty("default_diner_count", out var dinerCount);
        var hasStartupPage = body.TryGetProperty("startup_page", out var startupPage);
        var hasRepeatLookback = body.TryGetProperty(
            "randomizer_repeat_lookback_days", out var repeatLookback);
        var hasRepeatLookahead = body.TryGetProperty(
            "randomizer_repeat_lookahead_days", out var repeatLookahead);
        var hasRepeatMultiplier = body.TryGetProperty(
            "randomizer_repeat_multiplier", out var repeatMultiplier);
        var hasSpecialDayMultiplier = body.TryGetProperty(
            "randomizer_special_day_multiplier", out var specialDayMultiplier);
        var hasProteinMultiplier = body.TryGetProperty(
            "randomizer_protein_multiplier", out var proteinMultiplier);
        var hasProteinNeighborDistance = body.TryGetProperty(
            "randomizer_protein_neighbor_distance", out var proteinNeighborDistance);
        var hasFavoriteMultiplier = body.TryGetProperty(
            "randomizer_favorite_multiplier", out var favoriteMultiplier);
        var hasSpacingNearDays = body.TryGetProperty(
            "randomizer_spacing_near_days", out var spacingNearDays);
        var hasSpacingFarDays = body.TryGetProperty(
            "randomizer_spacing_far_days", out var spacingFarDays);
        var hasSpacingSearchDays = body.TryGetProperty(
            "randomizer_spacing_search_days", out var spacingSearchDays);

        var patch = new HouseholdSettingsPatch(
            hasHouseholdName ? ReadHouseholdName(householdName, errors) : null,
            hasTimezone ? ReadTimezone(timezone, errors) : null,
            hasWeekStartsOn ? ReadWeekStartsOn(weekStartsOn, errors) : null,
            hasDinerCount ? ReadDinerCount(dinerCount, errors) : null,
            hasStartupPage ? ReadStartupPage(startupPage, errors) : null,
            hasRepeatLookback
                ? ReadIntegerInRange(
                    repeatLookback,
                    "randomizer_repeat_lookback_days",
                    MinRandomizerRepeatLookbackDays,
                    MaxRandomizerRepeatLookbackDays,
                    errors)
                : null,
            hasRepeatLookahead
                ? ReadIntegerInRange(
                    repeatLookahead,
                    "randomizer_repeat_lookahead_days",
                    MinRandomizerRepeatLookaheadDays,
                    MaxRandomizerRepeatLookaheadDays,
                    errors)
                : null,
            hasRepeatMultiplier
                ? ReadDecimalInRange(
                    repeatMultiplier,
                    "randomizer_repeat_multiplier",
                    MinRandomizerRepeatMultiplier,
                    MaxRandomizerRepeatMultiplier,
                    errors)
                : null,
            hasSpecialDayMultiplier
                ? ReadDecimalInRange(
                    specialDayMultiplier,
                    "randomizer_special_day_multiplier",
                    MinRandomizerSpecialDayMultiplier,
                    MaxRandomizerSpecialDayMultiplier,
                    errors)
                : null,
            hasProteinMultiplier
                ? ReadDecimalInRange(
                    proteinMultiplier,
                    "randomizer_protein_multiplier",
                    MinRandomizerProteinMultiplier,
                    MaxRandomizerProteinMultiplier,
                    errors)
                : null,
            hasProteinNeighborDistance
                ? ReadIntegerInRange(
                    proteinNeighborDistance,
                    "randomizer_protein_neighbor_distance",
                    MinRandomizerProteinNeighborDistance,
                    MaxRandomizerProteinNeighborDistance,
                    errors)
                : null,
            hasFavoriteMultiplier
                ? ReadDecimalInRange(
                    favoriteMultiplier,
                    "randomizer_favorite_multiplier",
                    MinRandomizerFavoriteMultiplier,
                    MaxRandomizerFavoriteMultiplier,
                    errors)
                : null,
            hasSpacingNearDays
                ? ReadIntegerInRange(
                    spacingNearDays,
                    "randomizer_spacing_near_days",
                    MinRandomizerSpacingNearDays,
                    MaxRandomizerSpacingNearDays,
                    errors)
                : null,
            hasSpacingFarDays
                ? ReadIntegerInRange(
                    spacingFarDays,
                    "randomizer_spacing_far_days",
                    MinRandomizerSpacingFarDays,
                    MaxRandomizerSpacingFarDays,
                    errors)
                : null,
            hasSpacingSearchDays
                ? ReadIntegerInRange(
                    spacingSearchDays,
                    "randomizer_spacing_search_days",
                    MinRandomizerSpacingSearchDays,
                    MaxRandomizerSpacingSearchDays,
                    errors)
                : null);

        if (errors.Count > 0)
        {
            return new HouseholdSettingsPatchValidation(null, errors);
        }

        if (!hasHouseholdName
            && !hasTimezone
            && !hasWeekStartsOn
            && !hasDinerCount
            && !hasStartupPage
            && !hasRepeatLookback
            && !hasRepeatLookahead
            && !hasRepeatMultiplier
            && !hasSpecialDayMultiplier
            && !hasProteinMultiplier
            && !hasProteinNeighborDistance
            && !hasFavoriteMultiplier
            && !hasSpacingNearDays
            && !hasSpacingFarDays
            && !hasSpacingSearchDays)
        {
            return Invalid("body", "At least one household settings field must be provided.");
        }

        return new HouseholdSettingsPatchValidation(patch, NoFieldErrors);
    }

    internal static string? ReadHouseholdName(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            AddError(errors, "household_name", "household_name must be a string.");
            return null;
        }

        var value = element.GetString()!.Trim();

        if (value.Length == 0)
        {
            AddError(errors, "household_name", "household_name must not be empty or whitespace.");
            return null;
        }

        // PostgreSQL rejects U+0000 in text and C0 controls have no place in a
        // display name, so surface them as a validation error instead of a 500.
        if (ContainsControlCharacters(value))
        {
            AddError(errors, "household_name", "household_name must not contain control characters.");
            return null;
        }

        if (value.Length > MaxHouseholdNameLength)
        {
            AddError(
                errors,
                "household_name",
                $"household_name must be at most {MaxHouseholdNameLength} characters.");
            return null;
        }

        return value;
    }

    internal static string? ReadTimezone(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            AddError(errors, "timezone", "timezone must be a string containing an IANA timezone identifier.");
            return null;
        }

        var value = element.GetString()!.Trim();

        if (value.Length == 0)
        {
            AddError(errors, "timezone", "timezone must not be empty.");
            return null;
        }

        // The API accepts IANA timezone identifiers from the host tzdb (Linux)
        // and stores canonical casing: "america/chicago" is persisted as
        // "America/Chicago". Pseudo ids and unknown zones are rejected.
        if (!CanonicalTimeZoneIds.Value.TryGetValue(value, out var canonical))
        {
            AddError(errors, "timezone", "timezone must be a valid IANA timezone identifier.");
            return null;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(canonical);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            AddError(errors, "timezone", "timezone must be a valid IANA timezone identifier.");
            return null;
        }

        return canonical;
    }

    internal static int? ReadWeekStartsOn(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < MinWeekStartsOn
            || value > MaxWeekStartsOn)
        {
            AddError(
                errors,
                "week_starts_on",
                $"week_starts_on must be an integer between {MinWeekStartsOn} and {MaxWeekStartsOn} (0 = Sunday).");
            return null;
        }

        return value;
    }

    internal static int? ReadDinerCount(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < MinDinerCount
            || value > MaxDinerCount)
        {
            AddError(
                errors,
                "default_diner_count",
                $"default_diner_count must be an integer between {MinDinerCount} and {MaxDinerCount}.");
            return null;
        }

        return value;
    }

    internal static StartupPage? ReadStartupPage(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind == JsonValueKind.String
            && EnumTextConverters.TryStartupPageFromText(element.GetString()!, out var page))
        {
            return page;
        }

        AddError(errors, "startup_page", "startup_page must be \"calendar\" or \"dashboard\".");
        return null;
    }

    /// <summary>
    /// Enforces the cross-field randomizer constraints that require the current
    /// row: <c>randomizer_spacing_far_days</c> must be at least
    /// <c>randomizer_spacing_near_days</c>. Effective values combine the patch
    /// with the fields the patch leaves untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> ValidateAgainstCurrent(
        HouseholdSettingsPatch patch,
        HouseholdSettings current)
    {
        var effectiveNearDays = patch.RandomizerSpacingNearDays ?? current.RandomizerSpacingNearDays;
        var effectiveFarDays = patch.RandomizerSpacingFarDays ?? current.RandomizerSpacingFarDays;

        if (effectiveFarDays < effectiveNearDays)
        {
            return new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["randomizer_spacing_far_days"] =
                    [$"randomizer_spacing_far_days must be greater than or equal to randomizer_spacing_near_days ({effectiveNearDays})."],
            };
        }

        return NoFieldErrors;
    }

    private static int? ReadIntegerInRange(
        JsonElement element,
        string field,
        int min,
        int max,
        Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < min
            || value > max)
        {
            AddError(errors, field, $"{field} must be an integer between {min} and {max}.");
            return null;
        }

        return value;
    }

    private static decimal? ReadDecimalInRange(
        JsonElement element,
        string field,
        decimal min,
        decimal max,
        Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDecimal(out var value)
            || value < min
            || value > max)
        {
            AddError(
                errors,
                field,
                FormattableString.Invariant(
                    $"{field} must be a number between {min:0.00} and {max:0.00}."));
            return null;
        }

        return value;
    }

    internal static bool ContainsControlCharacters(string value)
    {
        foreach (var character in value)
        {
            if (character <= '\u001F' || character == '\u007F')
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalTimeZoneIds() =>
        BuildCanonicalTimeZoneIds(AddFileSystemTimeZones);

    /// <summary>
    /// Builds the canonical timezone map from
    /// <see cref="TimeZoneInfo.GetSystemTimeZones"/> plus the optional tzdata
    /// walk. The walk is best-effort: an unreadable or partially readable
    /// tzdata tree must not poison the lazy map (which would turn every
    /// timezone validation into a permanent 500), so IO failures fall back to
    /// the OS-provided list alone.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildCanonicalTimeZoneIds(
        Action<Dictionary<string, string>> addFileSystemTimeZones)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            canonical.TryAdd(timeZone.Id, timeZone.Id);
        }

        try
        {
            addFileSystemTimeZones(canonical);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // The OS list above is sufficient; ignore the failing walk.
        }

        return canonical;
    }

    private static void AddFileSystemTimeZones(Dictionary<string, string> canonical)
    {
        var root = Environment.GetEnvironmentVariable("TZDIR");
        if (string.IsNullOrEmpty(root))
        {
            root = "/usr/share/zoneinfo";
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var id = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

            // posix/ and right/ duplicate the zone tree with different
            // leap-second handling; the named files are helpers, not zone ids.
            if (id.StartsWith("posix/", StringComparison.Ordinal)
                || id.StartsWith("right/", StringComparison.Ordinal)
                || NonZoneFileNames.Contains(Path.GetFileName(id), StringComparer.Ordinal))
            {
                continue;
            }

            if (canonical.ContainsKey(id))
            {
                continue;
            }

            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            {
                canonical[id] = id;
            }
        }
    }

    private static void AddError(Dictionary<string, string[]> errors, string field, string message) =>
        errors[field] = [message];

    private static HouseholdSettingsPatchValidation Invalid(string field, string message) =>
        new(
            null,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

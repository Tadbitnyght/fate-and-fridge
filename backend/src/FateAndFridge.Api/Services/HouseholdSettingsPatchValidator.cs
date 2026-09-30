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
    StartupPage? StartupPage);

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

        // Unknown properties are ignored. When a key appears more than once,
        // JsonElement.TryGetProperty returns the last occurrence (last-one-wins);
        // no extra de-duplication is applied here.
        var hasHouseholdName = body.TryGetProperty("household_name", out var householdName);
        var hasTimezone = body.TryGetProperty("timezone", out var timezone);
        var hasWeekStartsOn = body.TryGetProperty("week_starts_on", out var weekStartsOn);
        var hasDinerCount = body.TryGetProperty("default_diner_count", out var dinerCount);
        var hasStartupPage = body.TryGetProperty("startup_page", out var startupPage);

        var patch = new HouseholdSettingsPatch(
            hasHouseholdName ? ReadHouseholdName(householdName, errors) : null,
            hasTimezone ? ReadTimezone(timezone, errors) : null,
            hasWeekStartsOn ? ReadWeekStartsOn(weekStartsOn, errors) : null,
            hasDinerCount ? ReadDinerCount(dinerCount, errors) : null,
            hasStartupPage ? ReadStartupPage(startupPage, errors) : null);

        if (errors.Count > 0)
        {
            return new HouseholdSettingsPatchValidation(null, errors);
        }

        if (!hasHouseholdName && !hasTimezone && !hasWeekStartsOn && !hasDinerCount && !hasStartupPage)
        {
            return Invalid("body", "At least one household settings field must be provided.");
        }

        return new HouseholdSettingsPatchValidation(patch, NoFieldErrors);
    }

    private static string? ReadHouseholdName(JsonElement element, Dictionary<string, string[]> errors)
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

    private static string? ReadTimezone(JsonElement element, Dictionary<string, string[]> errors)
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

    private static int? ReadWeekStartsOn(JsonElement element, Dictionary<string, string[]> errors)
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

    private static int? ReadDinerCount(JsonElement element, Dictionary<string, string[]> errors)
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

    private static StartupPage? ReadStartupPage(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind == JsonValueKind.String
            && EnumTextConverters.TryStartupPageFromText(element.GetString()!, out var page))
        {
            return page;
        }

        AddError(errors, "startup_page", "startup_page must be \"calendar\" or \"dashboard\".");
        return null;
    }

    private static bool ContainsControlCharacters(string value)
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

    private static IReadOnlyDictionary<string, string> BuildCanonicalTimeZoneIds()
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            canonical.TryAdd(timeZone.Id, timeZone.Id);
        }

        AddFileSystemTimeZones(canonical);

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

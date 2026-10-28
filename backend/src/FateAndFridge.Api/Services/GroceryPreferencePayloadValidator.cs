using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Parsed partial update of the caller's grocery preferences. Each member carries a "provided" flag so the service can
/// tell an absent field from an explicit <c>null</c> that clears the value.
/// </summary>
public sealed record GroceryPreferencePatch(
    bool HideCheckedProvided,
    bool HideChecked,
    bool PresetProvided,
    string? RangePreset,
    bool StartProvided,
    DateOnly? RangeStart,
    bool DaysProvided,
    int? RangeDays)
{
    /// <summary>True when the body supplied none of the known preference fields.</summary>
    public bool IsEmpty =>
        !HideCheckedProvided && !PresetProvided && !StartProvided && !DaysProvided;
}

/// <summary>Outcome of validating a preferences PATCH body.</summary>
public sealed record GroceryPreferencePatchValidation(
    GroceryPreferencePatch? Patch,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Parses and type-validates a <c>PATCH /groceries/preferences</c> body
/// Only per-field type/format checks live here; the
/// cross-field range normalization (which preset keeps which fields) runs in
/// <see cref="GroceryPreferenceService"/> where the stored row and household
/// today are known. Unknown JSON fields are ignored; a body with no known field
/// is rejected.
/// </summary>
public static class GroceryPreferencePayloadValidator
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>());

    /// <summary>Preset values accepted by <c>range_preset</c>.</summary>
    public static readonly IReadOnlyList<string> KnownPresets =
    [
        GroceryListService.PresetNext3Days,
        GroceryListService.PresetNext7Days,
        GroceryListService.PresetNext14Days,
        GroceryListService.PresetRestOfMonth,
        GroceryListService.PresetCustom,
    ];

    /// <summary>Validates the body and returns either a patch or field errors.</summary>
    public static GroceryPreferencePatchValidation Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body", "The request body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var hideCheckedProvided = body.TryGetProperty("hide_checked", out var hideCheckedElement);
        var hideChecked = false;
        if (hideCheckedProvided)
        {
            if (hideCheckedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                hideChecked = hideCheckedElement.GetBoolean();
            }
            else
            {
                errors["hide_checked"] = ["hide_checked must be a boolean."];
            }
        }

        var presetProvided = body.TryGetProperty("range_preset", out var presetElement);
        string? preset = null;
        if (presetProvided && presetElement.ValueKind != JsonValueKind.Null)
        {
            if (presetElement.ValueKind == JsonValueKind.String
                && KnownPresets.Contains(presetElement.GetString(), StringComparer.Ordinal))
            {
                preset = presetElement.GetString();
            }
            else
            {
                errors["range_preset"] =
                [
                    $"range_preset must be one of {string.Join(", ", KnownPresets)} or null.",
                ];
            }
        }

        var startProvided = body.TryGetProperty("range_start", out var startElement);
        DateOnly? start = null;
        if (startProvided && startElement.ValueKind != JsonValueKind.Null)
        {
            if (startElement.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    startElement.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStart))
            {
                start = parsedStart;
            }
            else
            {
                errors["range_start"] = ["range_start must be a YYYY-MM-DD date or null."];
            }
        }

        var daysProvided = body.TryGetProperty("range_days", out var daysElement);
        int? days = null;
        if (daysProvided && daysElement.ValueKind != JsonValueKind.Null)
        {
            if (daysElement.ValueKind == JsonValueKind.Number
                && daysElement.TryGetInt32(out var parsedDays))
            {
                days = parsedDays;
            }
            else
            {
                errors["range_days"] = ["range_days must be an integer or null."];
            }
        }

        if (errors.Count > 0)
        {
            return new GroceryPreferencePatchValidation(null, errors);
        }

        var patch = new GroceryPreferencePatch(
            hideCheckedProvided,
            hideChecked,
            presetProvided,
            preset,
            startProvided,
            start,
            daysProvided,
            days);

        if (patch.IsEmpty)
        {
            return Invalid("body", "At least one grocery preference field must be provided.");
        }

        return new GroceryPreferencePatchValidation(patch, NoFieldErrors);
    }

    private static GroceryPreferencePatchValidation Invalid(string field, string message) =>
        new(
            null,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Parsed <c>POST /groceries/manual</c> body (docs/M8_PLAN.md §1.8, §2.2).
/// Type/format/range checks are done by the validator; reference-data existence,
/// canonicalization, the household-today check, and the department default run
/// in <see cref="GroceryManualItemService"/> where the database is available.
/// </summary>
public sealed record GroceryManualItemCreateRequest(
    string Name,
    decimal? QuantityNumeric,
    long? UnitId,
    long? IngredientId,
    long? DepartmentId,
    DateOnly RangeStart,
    int RangeDays,
    bool SaveForFuture);

/// <summary>
/// Parsed partial update of a manual grocery item (docs/M8_PLAN.md §2.2). Each
/// member carries a "provided" flag so the service can tell an absent field from
/// an explicit <c>null</c> that clears a nullable value.
/// </summary>
public sealed record GroceryManualItemPatch(
    bool NameProvided,
    string? Name,
    bool QuantityProvided,
    decimal? QuantityNumeric,
    bool UnitProvided,
    long? UnitId,
    bool IngredientProvided,
    long? IngredientId,
    bool DepartmentProvided,
    long? DepartmentId,
    bool RangeStartProvided,
    DateOnly? RangeStart,
    bool RangeDaysProvided,
    int? RangeDays,
    bool SaveForFutureProvided,
    bool SaveForFuture)
{
    /// <summary>True when the body supplied none of the known manual-item fields.</summary>
    public bool IsEmpty =>
        !NameProvided
        && !QuantityProvided
        && !UnitProvided
        && !IngredientProvided
        && !DepartmentProvided
        && !RangeStartProvided
        && !RangeDaysProvided
        && !SaveForFutureProvided;
}

/// <summary>Outcome of validating a manual-item create body.</summary>
public sealed record GroceryManualItemCreateValidation(
    GroceryManualItemCreateRequest? Request,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>Outcome of validating a manual-item patch body.</summary>
public sealed record GroceryManualItemPatchValidation(
    GroceryManualItemPatch? Patch,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Parses and type-validates the manual grocery item bodies (docs/M8_PLAN.md
/// §1.8, §2.2). Unknown JSON fields are ignored; a patch body with no known
/// field is rejected. Only the name length/range and the quantity/date/day
/// format and static ranges are enforced here; a referenced id's existence is
/// checked in the service.
/// </summary>
public static class GroceryManualItemPayloadValidator
{
    /// <summary>Maximum manual item name length (docs/M8_PLAN.md §2.2).</summary>
    public const int MaxNameLength = 200;

    /// <summary>Inclusive lower bound for <c>range_days</c>.</summary>
    public const int MinRangeDays = 1;

    /// <summary>Inclusive upper bound for <c>range_days</c> (the 366-day cap).</summary>
    public const int MaxRangeDays = GroceryListService.MaxRangeDays;

    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>());

    /// <summary>Validates a create body and returns either a request or field errors.</summary>
    public static GroceryManualItemCreateValidation ValidateCreate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new GroceryManualItemCreateValidation(null, BodyError());
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = ReadName(body, required: true, errors);
        var quantity = ReadQuantity(body, errors);
        var unitId = ReadNullableLong(body, "unit_id", errors);
        var ingredientId = ReadNullableLong(body, "ingredient_id", errors);
        var departmentId = ReadNullableLong(body, "department_id", errors);
        var rangeStart = ReadRangeStart(body, required: true, errors);
        var rangeDays = ReadRangeDays(body, required: true, errors);
        var saveForFuture = ReadSaveForFuture(body, errors) ?? false;

        if (errors.Count > 0)
        {
            return new GroceryManualItemCreateValidation(null, errors);
        }

        return new GroceryManualItemCreateValidation(
            new GroceryManualItemCreateRequest(
                name!,
                quantity,
                unitId,
                ingredientId,
                departmentId,
                rangeStart!.Value,
                rangeDays!.Value,
                saveForFuture),
            NoFieldErrors);
    }

    /// <summary>Validates a patch body and returns either a patch or field errors.</summary>
    public static GroceryManualItemPatchValidation ValidatePatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new GroceryManualItemPatchValidation(null, BodyError());
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var nameProvided = body.TryGetProperty("name", out var nameElement);
        string? name = null;
        if (nameProvided)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                errors["name"] = ["name must be a string."];
            }
            else
            {
                name = ReadNameValue(nameElement.GetString()!, errors);
            }
        }

        var quantityProvided = body.TryGetProperty("quantity_numeric", out var quantityElement);
        decimal? quantity = null;
        if (quantityProvided && quantityElement.ValueKind != JsonValueKind.Null)
        {
            quantity = ReadQuantityValue(quantityElement, errors);
        }

        var unitProvided = ReadPatchLong(body, "unit_id", errors, out var unitId);
        var ingredientProvided = ReadPatchLong(body, "ingredient_id", errors, out var ingredientId);
        var departmentProvided = ReadPatchLong(body, "department_id", errors, out var departmentId);

        var rangeStartProvided = body.TryGetProperty("range_start", out var startElement);
        DateOnly? rangeStart = null;
        if (rangeStartProvided)
        {
            if (startElement.ValueKind == JsonValueKind.Null)
            {
                errors["range_start"] = ["range_start cannot be cleared."];
            }
            else if (startElement.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    startElement.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStart))
            {
                rangeStart = parsedStart;
            }
            else
            {
                errors["range_start"] = ["range_start must be a YYYY-MM-DD date."];
            }
        }

        var rangeDaysProvided = body.TryGetProperty("range_days", out var daysElement);
        int? rangeDays = null;
        if (rangeDaysProvided)
        {
            if (daysElement.ValueKind == JsonValueKind.Null)
            {
                errors["range_days"] = ["range_days cannot be cleared."];
            }
            else if (daysElement.ValueKind == JsonValueKind.Number
                && daysElement.TryGetInt32(out var parsedDays)
                && parsedDays >= MinRangeDays
                && parsedDays <= MaxRangeDays)
            {
                rangeDays = parsedDays;
            }
            else
            {
                errors["range_days"] =
                    [$"range_days must be an integer between {MinRangeDays} and {MaxRangeDays}."];
            }
        }

        var saveForFutureProvided = body.TryGetProperty("save_for_future", out var saveElement);
        var saveForFuture = false;
        if (saveForFutureProvided)
        {
            if (saveElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                saveForFuture = saveElement.GetBoolean();
            }
            else
            {
                errors["save_for_future"] = ["save_for_future must be a boolean."];
            }
        }

        if (errors.Count > 0)
        {
            return new GroceryManualItemPatchValidation(null, errors);
        }

        var patch = new GroceryManualItemPatch(
            nameProvided,
            name,
            quantityProvided,
            quantity,
            unitProvided,
            unitId,
            ingredientProvided,
            ingredientId,
            departmentProvided,
            departmentId,
            rangeStartProvided,
            rangeStart,
            rangeDaysProvided,
            rangeDays,
            saveForFutureProvided,
            saveForFuture);

        if (patch.IsEmpty)
        {
            return new GroceryManualItemPatchValidation(null, BodyError());
        }

        return new GroceryManualItemPatchValidation(patch, NoFieldErrors);
    }

    private static string? ReadName(
        JsonElement body,
        bool required,
        Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("name", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors["name"] = ["name is required."];
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors["name"] = ["name must be a string."];
            return null;
        }

        return ReadNameValue(element.GetString()!, errors);
    }

    private static string? ReadNameValue(string raw, Dictionary<string, string[]> errors)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            errors["name"] = ["name must not be empty or whitespace."];
            return null;
        }

        if (ContainsControlCharacters(value))
        {
            errors["name"] = ["name must not contain control characters."];
            return null;
        }

        if (value.Length > MaxNameLength)
        {
            errors["name"] = [$"name must be at most {MaxNameLength} characters."];
            return null;
        }

        return value;
    }

    private static decimal? ReadQuantity(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("quantity_numeric", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadQuantityValue(element, errors);
    }

    private static decimal? ReadQuantityValue(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDecimal(out var value)
            || value <= 0m)
        {
            errors["quantity_numeric"] = ["quantity_numeric must be a number greater than 0."];
            return null;
        }

        return value;
    }

    private static long? ReadNullableLong(
        JsonElement body,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        errors[field] = [$"{field} must be an integer."];
        return null;
    }

    private static bool ReadPatchLong(
        JsonElement body,
        string field,
        Dictionary<string, string[]> errors,
        out long? value)
    {
        value = null;
        if (!body.TryGetProperty(field, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed))
        {
            value = parsed;
            return true;
        }

        errors[field] = [$"{field} must be an integer or null."];
        return true;
    }

    private static DateOnly? ReadRangeStart(
        JsonElement body,
        bool required,
        Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("range_start", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors["range_start"] = ["range_start is required and must be a YYYY-MM-DD date."];
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(
                element.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return date;
        }

        errors["range_start"] = ["range_start must be a YYYY-MM-DD date."];
        return null;
    }

    private static int? ReadRangeDays(
        JsonElement body,
        bool required,
        Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("range_days", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors["range_days"] =
                    [$"range_days is required and must be between {MinRangeDays} and {MaxRangeDays}."];
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var days)
            && days >= MinRangeDays
            && days <= MaxRangeDays)
        {
            return days;
        }

        errors["range_days"] =
            [$"range_days must be an integer between {MinRangeDays} and {MaxRangeDays}."];
        return null;
    }

    private static bool? ReadSaveForFuture(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("save_for_future", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        errors["save_for_future"] = ["save_for_future must be a boolean."];
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

    private static IReadOnlyDictionary<string, string[]> BodyError() =>
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["body"] = ["The request body must be a valid JSON object with at least one field."],
        };
}

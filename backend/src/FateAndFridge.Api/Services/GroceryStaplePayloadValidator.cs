using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Parsed <c>POST /groceries/staples</c> body (docs/M8_PLAN.md §1.9, §2.2).
/// Type/format/range checks are done by the validator; reference-data existence,
/// canonicalization, and the department default run in
/// <see cref="GroceryStapleService"/> where the database is available.
/// </summary>
public sealed record GroceryStapleCreateRequest(
    string Name,
    long? IngredientId,
    long? DepartmentId,
    decimal? DefaultQuantityNumeric,
    long? UnitId);

/// <summary>
/// Parsed partial update of a household staple (docs/M8_PLAN.md §2.2). Each
/// member carries a "provided" flag so the service can tell an absent field from
/// an explicit <c>null</c> that clears a nullable value.
/// </summary>
public sealed record GroceryStaplePatch(
    bool NameProvided,
    string? Name,
    bool IngredientProvided,
    long? IngredientId,
    bool DepartmentProvided,
    long? DepartmentId,
    bool QuantityProvided,
    decimal? DefaultQuantityNumeric,
    bool UnitProvided,
    long? UnitId,
    bool IsActiveProvided,
    bool IsActive)
{
    /// <summary>True when the body supplied none of the known staple fields.</summary>
    public bool IsEmpty =>
        !NameProvided
        && !IngredientProvided
        && !DepartmentProvided
        && !QuantityProvided
        && !UnitProvided
        && !IsActiveProvided;
}

/// <summary>Parsed <c>POST /groceries/staples/{id}/add-to-list</c> body.</summary>
public sealed record GroceryStapleAddToListRequest(DateOnly RangeStart, int RangeDays);

/// <summary>Outcome of validating a staple create body.</summary>
public sealed record GroceryStapleCreateValidation(
    GroceryStapleCreateRequest? Request,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>Outcome of validating a staple patch body.</summary>
public sealed record GroceryStaplePatchValidation(
    GroceryStaplePatch? Patch,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>Outcome of validating an add-to-list body.</summary>
public sealed record GroceryStapleAddToListValidation(
    GroceryStapleAddToListRequest? Request,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Parses and type-validates the staple request bodies (docs/M8_PLAN.md §1.9,
/// §2.2). Only the name length/control rules, the positive quantity, the id
/// types, and the static range are enforced here; referenced ids, the
/// canonicalization, the department default, and the household-today check run in
/// <see cref="GroceryStapleService"/>. Unknown JSON fields are ignored; a patch
/// body with no known field is rejected.
/// </summary>
public static class GroceryStaplePayloadValidator
{
    /// <summary>Maximum staple name length (docs/M8_PLAN.md §2.2).</summary>
    public const int MaxNameLength = 200;

    /// <summary>Inclusive lower bound for the add-to-list <c>range_days</c>.</summary>
    public const int MinRangeDays = GroceryManualItemPayloadValidator.MinRangeDays;

    /// <summary>Inclusive upper bound for the add-to-list <c>range_days</c>.</summary>
    public const int MaxRangeDays = GroceryListService.MaxRangeDays;

    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>());

    /// <summary>Validates a create body and returns either a request or field errors.</summary>
    public static GroceryStapleCreateValidation ValidateCreate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new GroceryStapleCreateValidation(null, BodyError());
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = ReadName(body, required: true, errors);
        var ingredientId = ReadNullableLong(body, "ingredient_id", errors);
        var departmentId = ReadNullableLong(body, "department_id", errors);
        var quantity = ReadQuantity(body, errors);
        var unitId = ReadNullableLong(body, "unit_id", errors);

        if (errors.Count > 0)
        {
            return new GroceryStapleCreateValidation(null, errors);
        }

        return new GroceryStapleCreateValidation(
            new GroceryStapleCreateRequest(name!, ingredientId, departmentId, quantity, unitId),
            NoFieldErrors);
    }

    /// <summary>Validates a patch body and returns either a patch or field errors.</summary>
    public static GroceryStaplePatchValidation ValidatePatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new GroceryStaplePatchValidation(null, BodyError());
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var nameProvided = body.TryGetProperty("name", out var nameElement);
        string? name = null;
        if (nameProvided)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                // name may not be cleared, so an explicit null is rejected too.
                errors["name"] = ["name must be a string."];
            }
            else
            {
                name = ReadNameValue(nameElement.GetString()!, errors);
            }
        }

        var ingredientProvided = ReadPatchLong(body, "ingredient_id", errors, out var ingredientId);
        var departmentProvided = ReadPatchLong(body, "department_id", errors, out var departmentId);
        var unitProvided = ReadPatchLong(body, "unit_id", errors, out var unitId);

        var quantityProvided = body.TryGetProperty("default_quantity_numeric", out var quantityElement);
        decimal? quantity = null;
        if (quantityProvided && quantityElement.ValueKind != JsonValueKind.Null)
        {
            quantity = ReadQuantityValue(quantityElement, errors);
        }

        var isActiveProvided = body.TryGetProperty("is_active", out var activeElement);
        var isActive = true;
        if (isActiveProvided)
        {
            if (activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isActive = activeElement.GetBoolean();
            }
            else
            {
                errors["is_active"] = ["is_active must be a boolean."];
            }
        }

        if (errors.Count > 0)
        {
            return new GroceryStaplePatchValidation(null, errors);
        }

        var patch = new GroceryStaplePatch(
            nameProvided,
            name,
            ingredientProvided,
            ingredientId,
            departmentProvided,
            departmentId,
            quantityProvided,
            quantity,
            unitProvided,
            unitId,
            isActiveProvided,
            isActive);

        if (patch.IsEmpty)
        {
            return new GroceryStaplePatchValidation(null, BodyError());
        }

        return new GroceryStaplePatchValidation(patch, NoFieldErrors);
    }

    /// <summary>Validates an add-to-list body and returns either a range or field errors.</summary>
    public static GroceryStapleAddToListValidation ValidateAddToList(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new GroceryStapleAddToListValidation(null, BodyError());
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var rangeStart = ReadRangeStart(body, errors);
        var rangeDays = ReadRangeDays(body, errors);

        if (errors.Count > 0)
        {
            return new GroceryStapleAddToListValidation(null, errors);
        }

        return new GroceryStapleAddToListValidation(
            new GroceryStapleAddToListRequest(rangeStart!.Value, rangeDays!.Value),
            NoFieldErrors);
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
        if (!body.TryGetProperty("default_quantity_numeric", out var element)
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
            errors["default_quantity_numeric"] =
                ["default_quantity_numeric must be a number greater than 0."];
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

    private static DateOnly? ReadRangeStart(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("range_start", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors["range_start"] = ["range_start is required and must be a YYYY-MM-DD date."];
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

    private static int? ReadRangeDays(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("range_days", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors["range_days"] =
                [$"range_days is required and must be between {MinRangeDays} and {MaxRangeDays}."];
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

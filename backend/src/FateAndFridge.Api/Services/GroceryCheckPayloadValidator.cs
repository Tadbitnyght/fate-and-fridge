using System.Text.Json;

namespace FateAndFridge.Api.Services;

/// <summary>Outcome of validating a check/uncheck body.</summary>
public sealed record GroceryCheckPayloadValidation(
    string? Identity,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Parses and validates the <c>POST /groceries/check</c> and
/// <c>/uncheck</c> bodies. The only accepted value
/// is the exact<see cref="GroceryItemIdentity"/> format: 64 lowercase hex
/// characters.
/// </summary>
public static class GroceryCheckPayloadValidator
{
    /// <summary>Length of a <see cref="GroceryItemIdentity"/> hash.</summary>
    public const int IdentityLength = 64;

    /// <summary>Validates the body and returns either an identity or field errors.</summary>
    public static GroceryCheckPayloadValidation Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body", "The request body must be a JSON object.");
        }

        if (!body.TryGetProperty("identity", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return Invalid("identity", "identity is required and must be a 64-character lowercase hex string.");
        }

        var identity = element.GetString()!;
        return IsIdentity(identity)
            ? new GroceryCheckPayloadValidation(identity, NoFieldErrors)
            : Invalid("identity", "identity must be a 64-character lowercase hex string.");
    }

    /// <summary>True when the value is exactly 64 lowercase hex characters.</summary>
    public static bool IsIdentity(string value)
    {
        if (value.Length != IdentityLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLowerHex = (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string[]>(
            new Dictionary<string, string[]>());

    private static GroceryCheckPayloadValidation Invalid(string field, string message) =>
        new(
            null,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

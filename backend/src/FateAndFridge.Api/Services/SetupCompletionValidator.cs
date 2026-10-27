using System.Collections.ObjectModel;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>Validated first-Admin credentials for setup completion (docs/API.md §21).</summary>
public sealed record SetupAdminCredentials(string Username, string NormalizedUsername, string Password);

/// <summary>Validated setup completion request (docs/API.md §21).</summary>
public sealed record SetupCompletionRequest(
    string HouseholdName,
    string Timezone,
    int WeekStartsOn,
    int DefaultDinerCount,
    StartupPage StartupPage,
    AuthMode AuthMode,
    bool UpdateCheckEnabled,
    SetupAdminCredentials? Admin,
    bool IpAllowlistEnabled,
    IReadOnlyList<string> IpAllowlistRanges);

/// <summary>
/// Outcome of validating a setup completion body. <see cref="Request"/> is null
/// when any field failed validation.
/// </summary>
public sealed record SetupCompletionValidation(
    SetupCompletionRequest? Request,
    IReadOnlyDictionary<string, string[]> FieldErrors);

/// <summary>
/// Validates the single-shot setup completion body (docs/API.md §21). Household
/// fields reuse <see cref="HouseholdSettingsPatchValidator"/> rules; the Admin
/// block is required if and only if <c>auth_mode</c> is <c>multiuser</c>; IP
/// allowlist ranges must be valid IPv4/IPv6 CIDR when provided, and at least one
/// range is required when the allowlist is enabled.
/// </summary>
public static class SetupCompletionValidator
{
    public const int MinUsernameLength = 3;

    public const int MaxUsernameLength = 32;

    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>());

    /// <summary>Validates the body and returns either a request or field errors.</summary>
    public static SetupCompletionValidation Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body", "The request body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var householdName = ReadRequiredHouseholdField(
            body,
            "household_name",
            errors,
            HouseholdSettingsPatchValidator.ReadHouseholdName);
        var timezone = ReadRequiredHouseholdField(
            body,
            "timezone",
            errors,
            HouseholdSettingsPatchValidator.ReadTimezone);
        var weekStartsOn = ReadRequiredHouseholdField(
            body,
            "week_starts_on",
            errors,
            HouseholdSettingsPatchValidator.ReadWeekStartsOn);
        var dinerCount = ReadRequiredHouseholdField(
            body,
            "default_diner_count",
            errors,
            HouseholdSettingsPatchValidator.ReadDinerCount);
        var startupPage = ReadRequiredHouseholdField(
            body,
            "startup_page",
            errors,
            HouseholdSettingsPatchValidator.ReadStartupPage);

        AuthMode? authMode = null;
        if (body.TryGetProperty("auth_mode", out var authModeElement)
            && authModeElement.ValueKind == JsonValueKind.String
            && EnumTextConverters.TryAuthModeFromText(authModeElement.GetString(), out var parsedAuthMode))
        {
            authMode = parsedAuthMode;
        }
        else
        {
            AddError(errors, "auth_mode", "auth_mode must be \"single_user\" or \"multiuser\".");
        }

        bool? updateCheckEnabled = null;
        if (body.TryGetProperty("update_check_enabled", out var updateCheckElement)
            && updateCheckElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            updateCheckEnabled = updateCheckElement.GetBoolean();
        }
        else
        {
            AddError(errors, "update_check_enabled", "update_check_enabled must be a boolean.");
        }

        var admin = ReadAdmin(body, authMode, errors);
        var (ipAllowlistEnabled, ipAllowlistRanges) = ReadIpAllowlist(body, errors);

        if (errors.Count > 0)
        {
            return new SetupCompletionValidation(null, errors);
        }

        return new SetupCompletionValidation(
            new SetupCompletionRequest(
                householdName!,
                timezone!,
                weekStartsOn!.Value,
                dinerCount!.Value,
                startupPage!.Value,
                authMode!.Value,
                updateCheckEnabled!.Value,
                admin,
                ipAllowlistEnabled,
                ipAllowlistRanges),
            NoFieldErrors);
    }

    private static T? ReadRequiredHouseholdField<T>(
        JsonElement body,
        string fieldName,
        Dictionary<string, string[]> errors,
        Func<JsonElement, Dictionary<string, string[]>, T?> read)
        where T : class
    {
        if (body.TryGetProperty(fieldName, out var element))
        {
            return read(element, errors);
        }

        AddError(errors, fieldName, $"{fieldName} is required.");
        return null;
    }

    private static int? ReadRequiredHouseholdField(
        JsonElement body,
        string fieldName,
        Dictionary<string, string[]> errors,
        Func<JsonElement, Dictionary<string, string[]>, int?> read)
    {
        if (body.TryGetProperty(fieldName, out var element))
        {
            return read(element, errors);
        }

        AddError(errors, fieldName, $"{fieldName} is required.");
        return null;
    }

    private static StartupPage? ReadRequiredHouseholdField(
        JsonElement body,
        string fieldName,
        Dictionary<string, string[]> errors,
        Func<JsonElement, Dictionary<string, string[]>, StartupPage?> read)
    {
        if (body.TryGetProperty(fieldName, out var element))
        {
            return read(element, errors);
        }

        AddError(errors, fieldName, $"{fieldName} is required.");
        return null;
    }

    private static SetupAdminCredentials? ReadAdmin(
        JsonElement body,
        AuthMode? authMode,
        Dictionary<string, string[]> errors)
    {
        var hasAdmin = body.TryGetProperty("admin", out var adminElement);

        if (authMode == AuthMode.Multiuser)
        {
            if (!hasAdmin || adminElement.ValueKind != JsonValueKind.Object)
            {
                AddError(errors, "admin", "admin is required when auth_mode is \"multiuser\".");
                return null;
            }

            return ReadAdminCredentials(adminElement, errors);
        }

        if (authMode == AuthMode.SingleUser && hasAdmin)
        {
            AddError(errors, "admin", "admin must not be provided when auth_mode is \"single_user\".");
        }

        return null;
    }

    internal static SetupAdminCredentials? ReadAdminCredentials(
        JsonElement adminElement,
        Dictionary<string, string[]> errors)
    {
        string? username = null;
        string? normalizedUsername = null;
        string? password = null;

        if (!adminElement.TryGetProperty("username", out var usernameElement)
            || usernameElement.ValueKind != JsonValueKind.String)
        {
            AddError(errors, "admin.username", "admin.username is required and must be a string.");
        }
        else
        {
            var trimmed = usernameElement.GetString()!.Trim();
            if (!UsernamePolicy.IsValid(trimmed))
            {
                AddError(
                    errors,
                    "admin.username",
                    $"admin.username must be between {MinUsernameLength} and {MaxUsernameLength} characters using letters, digits, '.', '_' or '-'.");
            }
            else
            {
                username = trimmed;
                normalizedUsername = UsernameNormalizer.Normalize(trimmed);
            }
        }

        if (!adminElement.TryGetProperty("password", out var passwordElement)
            || passwordElement.ValueKind != JsonValueKind.String)
        {
            AddError(errors, "admin.password", "admin.password is required and must be a string.");
        }
        else if (!PasswordPolicy.IsValid(passwordElement.GetString()))
        {
            AddError(
                errors,
                "admin.password",
                $"admin.password must be between {PasswordPolicy.MinLength} and {PasswordPolicy.MaxLength} characters.");
        }
        else
        {
            password = passwordElement.GetString()!;
        }

        return username is null || password is null
            ? null
            : new SetupAdminCredentials(username, normalizedUsername!, password);
    }

    private static (bool Enabled, IReadOnlyList<string> Ranges) ReadIpAllowlist(
        JsonElement body,
        Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("ip_allowlist", out var allowlistElement))
        {
            return (false, []);
        }

        if (allowlistElement.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, "ip_allowlist", "ip_allowlist must be an object.");
            return (false, []);
        }

        var enabled = false;
        if (allowlistElement.TryGetProperty("enabled", out var enabledElement)
            && enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = enabledElement.GetBoolean();
        }
        else
        {
            AddError(errors, "ip_allowlist.enabled", "ip_allowlist.enabled must be a boolean.");
        }

        var ranges = new List<string>();
        if (allowlistElement.TryGetProperty("ranges", out var rangesElement))
        {
            if (rangesElement.ValueKind != JsonValueKind.Array)
            {
                AddError(errors, "ip_allowlist.ranges", "ip_allowlist.ranges must be an array of CIDR strings.");
            }
            else
            {
                foreach (var rangeElement in rangesElement.EnumerateArray())
                {
                    var range = rangeElement.ValueKind == JsonValueKind.String
                        ? rangeElement.GetString()!.Trim()
                        : null;

                    if (range is null || !ClientAddress.TryNormalizeRange(range, out var cidr))
                    {
                        AddError(
                            errors,
                            "ip_allowlist.ranges",
                            "Each ip_allowlist range must be a valid IPv4 or IPv6 address or CIDR.");
                        break;
                    }

                    ranges.Add(cidr);
                }
            }
        }

        if (enabled && ranges.Count == 0 && !errors.ContainsKey("ip_allowlist.ranges"))
        {
            AddError(
                errors,
                "ip_allowlist.ranges",
                "At least one ip_allowlist range is required when the allowlist is enabled.");
        }

        return (enabled, ranges);
    }

    private static void AddError(Dictionary<string, string[]> errors, string field, string message) =>
        errors[field] = [message];

    private static SetupCompletionValidation Invalid(string field, string message) =>
        new(
            null,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

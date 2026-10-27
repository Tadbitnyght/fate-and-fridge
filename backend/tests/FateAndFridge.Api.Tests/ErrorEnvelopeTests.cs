using System.Text.Json;
using FateAndFridge.Api.Errors;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Locks the shared error envelope shape and the stable M1 error codes
/// (docs/API.md §4). Clients branch on these values.
/// </summary>
public sealed class ErrorEnvelopeTests
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Error_envelope_serializes_as_error_code_message_details()
    {
        var envelope = new ApiErrorEnvelope(
            new ApiError(
                ErrorCodes.SetupRequired,
                "Setup is incomplete.",
                new Dictionary<string, object?>()));

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);

        json.Should().Be(
            """{"error":{"code":"SETUP_REQUIRED","message":"Setup is incomplete.","details":{}}}""");
    }

    [Fact]
    public void Validation_details_use_field_errors()
    {
        var details = new Dictionary<string, object?>
        {
            ["field_errors"] = new Dictionary<string, string[]>
            {
                ["timezone"] = ["The timezone is not a known IANA timezone."],
            },
        };

        var json = JsonSerializer.Serialize(
            new ApiErrorEnvelope(
                new ApiError(ErrorCodes.ValidationFailed, "The request failed validation.", details)),
            SerializerOptions);

        json.Should().Contain("field_errors");
        json.Should().Contain("timezone");
    }

    [Theory]
    [InlineData("SETUP_REQUIRED", ErrorCodes.SetupRequired)]
    [InlineData("SETUP_ALREADY_COMPLETED", ErrorCodes.SetupAlreadyCompleted)]
    [InlineData("VALIDATION_FAILED", ErrorCodes.ValidationFailed)]
    [InlineData("AUTH_REQUIRED", ErrorCodes.AuthRequired)]
    [InlineData("INVALID_CREDENTIALS", ErrorCodes.InvalidCredentials)]
    [InlineData("ACCOUNT_LOCKED", ErrorCodes.AccountLocked)]
    [InlineData("RATE_LIMITED", ErrorCodes.RateLimited)]
    [InlineData("PASSWORD_CHANGE_REQUIRED", ErrorCodes.PasswordChangeRequired)]
    [InlineData("FORBIDDEN", ErrorCodes.Forbidden)]
    [InlineData("IP_NOT_ALLOWED", ErrorCodes.IpNotAllowed)]
    [InlineData("AUTH_MODE_DOWNGRADE_NOT_SUPPORTED", ErrorCodes.AuthModeDowngradeNotSupported)]
    public void Error_codes_match_the_documented_contract(string expected, string actual) =>
        actual.Should().Be(expected);
}

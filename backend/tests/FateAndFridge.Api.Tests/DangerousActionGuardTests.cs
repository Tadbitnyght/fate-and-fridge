using System.Globalization;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit tests for the shared dangerous-action guard. No database or HTTP is involved: the guard is the server-side
/// authority every dangerous endpoint (historical correction, hard delete, audit
/// prune, M13 restore) must call.
/// </summary>
public sealed class DangerousActionGuardTests
{
    private const string ExpectedConfirmation = "prune_all";

    [Fact]
    public void A_trimmed_reason_is_accepted()
    {
        var result = DangerousActionGuard.Validate("  Keep the last year only  ", null, null);

        var success = result.Should().BeOfType<DangerousActionValidation.Success>().Subject;
        success.Reason.Should().Be("Keep the last year only");
    }

    [Fact]
    public void A_reason_with_a_typed_confirmation_is_accepted()
    {
        var result = DangerousActionGuard.Validate(
            "Retention window has passed",
            "  prune_all ",
            ExpectedConfirmation);

        var success = result.Should().BeOfType<DangerousActionValidation.Success>().Subject;
        success.Reason.Should().Be("Retention window has passed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void A_blank_reason_is_rejected_with_the_reason_code(string? reason)
    {
        var result = DangerousActionGuard.Validate(reason, null, null);

        var failure = result.Should().BeOfType<DangerousActionValidation.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.ReasonRequired);
        failure.FieldErrors.Should().ContainKey(DangerousActionGuard.ReasonField);
        failure.FieldErrors[DangerousActionGuard.ReasonField].Should().ContainSingle();
    }

    [Fact]
    public void A_reason_at_the_maximum_length_is_accepted()
    {
        var reason = new string('x', DangerousActionGuard.MaxReasonLength);

        DangerousActionGuard.Validate(reason, null, null)
            .Should().BeOfType<DangerousActionValidation.Success>();
    }

    [Fact]
    public void A_reason_over_the_maximum_length_is_rejected_after_trimming()
    {
        var reason = $"  {new string('x', DangerousActionGuard.MaxReasonLength + 1)}  ";

        var failure = DangerousActionGuard.Validate(reason, null, null)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        failure.Code.Should().Be(ErrorCodes.ReasonTooLong);
        failure.FieldErrors[DangerousActionGuard.ReasonField]
            .Should().ContainSingle()
            .Which.Should().Contain(
                DangerousActionGuard.MaxReasonLength.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_null_character_in_the_reason_is_rejected()
    {
        var failure = DangerousActionGuard.Validate("bad\u0000reason", null, null)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        failure.Code.Should().Be(ErrorCodes.ReasonInvalid);
        failure.FieldErrors.Should().ContainKey(DangerousActionGuard.ReasonField);
    }

    [Fact]
    public void Multi_line_reasons_keep_their_newlines_and_tabs()
    {
        var success = DangerousActionGuard.Validate("first line\n\tsecond line", null, null)
            .Should().BeOfType<DangerousActionValidation.Success>().Subject;

        success.Reason.Should().Be("first line\n\tsecond line");
    }

    [Fact]
    public void A_missing_confirmation_is_rejected_when_one_is_expected()
    {
        var failure = DangerousActionGuard.Validate("why", null, ExpectedConfirmation)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        failure.Code.Should().Be(ErrorCodes.ConfirmationMismatch);
        failure.FieldErrors.Should().ContainKey(DangerousActionGuard.ConfirmationField);
        failure.FieldErrors[DangerousActionGuard.ConfirmationField].Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("prune_al")]
    [InlineData("prune_allx")]
    [InlineData("PRUNE_ALL")]
    [InlineData("Prune All")]
    public void A_mismatched_confirmation_is_rejected(string confirmation)
    {
        var failure = DangerousActionGuard.Validate("why", confirmation, ExpectedConfirmation)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        failure.Code.Should().Be(ErrorCodes.ConfirmationMismatch);
        failure.FieldErrors.Should().ContainKey(DangerousActionGuard.ConfirmationField);
    }

    [Theory]
    [InlineData("prune_all")]
    [InlineData("  prune_all  ")]
    [InlineData("\tprune_all\n")]
    public void An_exact_or_whitespace_padded_confirmation_is_accepted(string confirmation)
    {
        DangerousActionGuard.Validate("why", confirmation, ExpectedConfirmation)
            .Should().BeOfType<DangerousActionValidation.Success>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_expected_confirmation_requires_no_typed_value(string? expected)
    {
        DangerousActionGuard.Validate("why", null, expected)
            .Should().BeOfType<DangerousActionValidation.Success>();
    }

    [Fact]
    public void The_expected_confirmation_is_trimmed_before_comparison()
    {
        DangerousActionGuard.Validate("why", "prune_all", "  prune_all  ")
            .Should().BeOfType<DangerousActionValidation.Success>();
    }

    [Fact]
    public void Reason_failures_take_priority_over_confirmation_failures()
    {
        var failure = DangerousActionGuard.Validate("  ", "wrong", ExpectedConfirmation)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        failure.Code.Should().Be(ErrorCodes.ReasonRequired);
        failure.FieldErrors.Should().NotContainKey(DangerousActionGuard.ConfirmationField);
    }

    [Fact]
    public void A_failure_exposes_the_shared_envelope_details()
    {
        var failure = DangerousActionGuard.Validate(null, null, null)
            .Should().BeOfType<DangerousActionValidation.Failure>().Subject;

        var details = failure.ToErrorDetails();

        details.Should().ContainKey("field_errors");
        details["field_errors"].Should().BeSameAs(failure.FieldErrors);
        failure.Message.Should().NotBeNullOrWhiteSpace();
    }
}

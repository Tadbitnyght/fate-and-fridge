using System.Collections.ObjectModel;
using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Result of <see cref="DangerousActionGuard.Validate"/> (docs/M12_PLAN.md
/// §1.11): the trimmed reason on success, or the shared error code plus
/// per-field errors on failure.
/// </summary>
public abstract record DangerousActionValidation
{
    private DangerousActionValidation()
    {
    }

    /// <summary>The request is acceptable; carries the reason to persist and audit.</summary>
    public sealed record Success(string Reason) : DangerousActionValidation;

    /// <summary>
    /// The request is rejected. <see cref="Code"/> is one of the M12
    /// dangerous-action codes (docs/API.md §4); <see cref="FieldErrors"/> maps
    /// the offending wire field to its message(s).
    /// </summary>
    public sealed record Failure(
        string Code,
        string Message,
        IReadOnlyDictionary<string, string[]> FieldErrors) : DangerousActionValidation
    {
        /// <summary>
        /// The details object for the shared error envelope
        /// (field_errors), ready for <see cref="ApiResults.Error"/>.
        /// </summary>
        public IReadOnlyDictionary<string, object?> ToErrorDetails() =>
            new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field_errors"] = FieldErrors,
                });
    }
}

/// <summary>
/// Server-side guard for dangerous admin actions (docs/M12_PLAN.md §1.11,
/// docs/SECURITY.md §10): a non-blank, bounded reason and an exact typed
/// confirmation. The dialog is a convenience only — this runs on every request,
/// independently of the UI, and is shared by historical correction, hard recipe
/// delete, audit prune, and M13 restore so the rules cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is ordinal (case-sensitive) after trimming only leading and
/// trailing whitespace, and deliberately performs no Unicode normalization: the
/// process runs with invariant globalization for other code paths, so
/// normalization would be a silent no-op there and behaviour must not depend on
/// it (AGENTS.md).
/// </para>
/// </remarks>
public static class DangerousActionGuard
{
    /// <summary>
    /// Maximum accepted reason length, in UTF-16 code units (locked in
    /// docs/M12_PLAN.md §1.11). Long enough for a sentence or two, bounded
    /// because the reason is stored on every audit row.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>Wire field name of the reason in details.field_errors.</summary>
    public const string ReasonField = "reason";

    /// <summary>
    /// Wire field name of the typed confirmation in details.field_errors.
    /// Callers whose contract names the request field differently (for example
    /// the confirm field in docs/M12_PLAN.md §2.2) remap it here.
    /// </summary>
    public const string ConfirmationField = "confirmation";

    /// <summary>
    /// Validates a dangerous-action request. When
    /// <paramref name="expectedConfirmation"/> is null or whitespace the caller
    /// requires no typed confirmation and <paramref name="confirmation"/> is
    /// ignored; otherwise the supplied value must match the expected value
    /// exactly after trimming. Reason failures are reported before confirmation
    /// failures, so a request missing both returns
    /// <see cref="ErrorCodes.ReasonRequired"/>.
    /// </summary>
    public static DangerousActionValidation Validate(
        string? reason,
        string? confirmation,
        string? expectedConfirmation)
    {
        var trimmedReason = reason?.Trim();

        if (string.IsNullOrEmpty(trimmedReason))
        {
            return Failure(
                ErrorCodes.ReasonRequired,
                "A reason is required for this action.",
                ReasonField,
                "reason is required.");
        }

        if (trimmedReason.Length > MaxReasonLength)
        {
            return Failure(
                ErrorCodes.ReasonTooLong,
                $"The reason must be at most {MaxReasonLength} characters.",
                ReasonField,
                $"reason must be at most {MaxReasonLength} characters.");
        }

        // PostgreSQL rejects U+0000 in text; newlines and tabs are valid in a
        // multi-line reason, so only the null character is rejected here — as
        // validation rather than a 500.
        if (ContainsNullCharacter(trimmedReason))
        {
            return Failure(
                ErrorCodes.ReasonInvalid,
                "The reason must not contain a null character.",
                ReasonField,
                "reason must not contain a null character.");
        }

        if (!string.IsNullOrWhiteSpace(expectedConfirmation))
        {
            var normalizedConfirmation = confirmation?.Trim();

            if (!string.Equals(
                    normalizedConfirmation,
                    expectedConfirmation.Trim(),
                    StringComparison.Ordinal))
            {
                return Failure(
                    ErrorCodes.ConfirmationMismatch,
                    "The typed confirmation does not match the required value.",
                    ConfirmationField,
                    "confirmation does not match the required value.");
            }
        }

        return new DangerousActionValidation.Success(trimmedReason);
    }

    private static bool ContainsNullCharacter(string value)
    {
        foreach (var character in value)
        {
            if (character == '\u0000')
            {
                return true;
            }
        }

        return false;
    }

    private static DangerousActionValidation.Failure Failure(
        string code,
        string message,
        string field,
        string fieldMessage) =>
        new(
            code,
            message,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [fieldMessage] });
}

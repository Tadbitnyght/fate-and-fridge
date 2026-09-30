using System.Collections.ObjectModel;

namespace FateAndFridge.Api.Errors;

/// <summary>
/// Creates the shared error envelope with an explicit HTTP status code, as
/// required by docs/API.md §4. The status is passed by the caller because the
/// contract fixes it per error code (for example <c>SETUP_REQUIRED</c> is 409).
/// </summary>
public static class ApiResults
{
    private static readonly IReadOnlyDictionary<string, object?> NoDetails =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    /// <summary>Returns the shared error envelope with the given status code.</summary>
    public static IResult Error(
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null) =>
        Results.Json(
            new ApiErrorEnvelope(new ApiError(code, message, details ?? NoDetails)),
            statusCode: statusCode);

    /// <summary>
    /// Returns a <c>400 VALIDATION_FAILED</c> envelope whose
    /// <c>details.field_errors</c> maps each invalid field to its messages.
    /// </summary>
    public static IResult ValidationError(IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        var details = new Dictionary<string, object?> { ["field_errors"] = fieldErrors };
        return Error(
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            "The request failed validation.",
            details);
    }
}

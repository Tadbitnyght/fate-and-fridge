namespace FateAndFridge.Api.Errors;

/// <summary>
/// Stable API error codes (docs/API.md §4). Clients branch on these values,
/// never on <c>message</c>.
/// </summary>
public static class ErrorCodes
{
    public const string SetupRequired = "SETUP_REQUIRED";

    public const string SetupAlreadyCompleted = "SETUP_ALREADY_COMPLETED";

    public const string ValidationFailed = "VALIDATION_FAILED";

    public const string AuthRequired = "AUTH_REQUIRED";

    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";

    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    public const string AccountLocked = "ACCOUNT_LOCKED";

    public const string RateLimited = "RATE_LIMITED";

    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";

    public const string NotFound = "NOT_FOUND";

    public const string InternalError = "INTERNAL_ERROR";

    public const string Forbidden = "FORBIDDEN";

    public const string IpNotAllowed = "IP_NOT_ALLOWED";

    public const string AuthModeDowngradeNotSupported = "AUTH_MODE_DOWNGRADE_NOT_SUPPORTED";
}

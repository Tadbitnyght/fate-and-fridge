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

    public const string LastAdminProtected = "LAST_ADMIN_PROTECTED";

    public const string AuthModeAlreadyMultiuser = "AUTH_MODE_ALREADY_MULTIUSER";

    public const string Forbidden = "FORBIDDEN";

    public const string IpNotAllowed = "IP_NOT_ALLOWED";

    public const string AuthModeDowngradeNotSupported = "AUTH_MODE_DOWNGRADE_NOT_SUPPORTED";

    // M2 reference-data codes (docs/API.md §22).
    public const string DepartmentInUse = "DEPARTMENT_IN_USE";

    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";

    public const string UnitExists = "UNIT_EXISTS";

    public const string AliasExists = "ALIAS_EXISTS";

    public const string UnitInUse = "UNIT_IN_USE";

    public const string IngredientExists = "INGREDIENT_EXISTS";

    public const string IngredientInUse = "INGREDIENT_IN_USE";

    public const string InvalidUnitDimensionChange = "INVALID_UNIT_DIMENSION_CHANGE";
}

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

    public const string ProteinExists = "PROTEIN_EXISTS";

    public const string ProteinInUse = "PROTEIN_IN_USE";

    public const string TagExists = "TAG_EXISTS";

    public const string TagInUse = "TAG_IN_USE";

    // M3 recipe codes (docs/M3_PLAN.md §1.2).
    public const string RecipeExists = "RECIPE_EXISTS";

    // M3-04 image content policy codes (docs/M3_PLAN.md §1.7).
    public const string ImageTypeNotAllowed = "IMAGE_TYPE_NOT_ALLOWED";

    public const string ImageInvalid = "IMAGE_INVALID";

    public const string ImageTooLarge = "IMAGE_TOO_LARGE";

    public const string ImageDimensionsExceeded = "IMAGE_DIMENSIONS_EXCEEDED";

    // M4 import codes (docs/M4_PLAN.md §2.2, docs/API.md §15).
    public const string ImportUrlInvalid = "IMPORT_URL_INVALID";

    public const string ImportUrlBlocked = "IMPORT_URL_BLOCKED";

    public const string ImportFetchFailed = "IMPORT_FETCH_FAILED";

    public const string ImportUnsupportedContent = "IMPORT_UNSUPPORTED_CONTENT";

    public const string ImportTooLarge = "IMPORT_TOO_LARGE";

    public const string ImportNoRecipe = "IMPORT_NO_RECIPE";

    public const string ImportRateLimited = "IMPORT_RATE_LIMITED";

    // M5 calendar codes (docs/M5_PLAN.md §1.12).
    public const string CalendarDateInPast = "CALENDAR_DATE_IN_PAST";

    public const string CalendarBlankCannotLock = "CALENDAR_BLANK_CANNOT_LOCK";

    /// <summary>Reserved for M6/M7 automatic mutation of a locked date.</summary>
    public const string CalendarEntryLocked = "CALENDAR_ENTRY_LOCKED";

    public const string CalendarRecipeRequired = "CALENDAR_RECIPE_REQUIRED";

    public const string CalendarMiscLabelRequired = "CALENDAR_MISC_LABEL_REQUIRED";

    // M6 randomizer codes (docs/M6_PLAN.md §2.2).
    /// <summary>No <c>Normal</c> recipe exists to auto-select.</summary>
    public const string RandomizerNoCandidates = "RANDOMIZER_NO_CANDIDATES";

    /// <summary>The entry type cannot be rerolled (vacation).</summary>
    public const string RandomizerNotApplicable = "RANDOMIZER_NOT_APPLICABLE";

    /// <summary>Reserved for M6-09 undo: the operation is missing or already undone.</summary>
    public const string UndoExpired = "UNDO_EXPIRED";

    /// <summary>Reserved for M6-09 undo: a later change invalidated the undo.</summary>
    public const string UndoConflict = "UNDO_CONFLICT";

    // M7 batch-planning codes (docs/M7_PLAN.md §2.2).
    /// <summary>Reserved for M7-03 apply: a target date changed since the preview.</summary>
    public const string BatchConflict = "BATCH_CONFLICT";

    /// <summary>Reserved for M7-03 apply: the draft is missing, expired, or already consumed.</summary>
    public const string BatchDraftExpired = "BATCH_DRAFT_EXPIRED";
}

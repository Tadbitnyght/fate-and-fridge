/**
 * Stable M1 error codes (docs/API.md §4). Clients branch on these, never on
 * the human-readable message.
 */
export const ErrorCodes = {
  SetupRequired: 'SETUP_REQUIRED',
  SetupAlreadyCompleted: 'SETUP_ALREADY_COMPLETED',
  ValidationFailed: 'VALIDATION_FAILED',
  AuthRequired: 'AUTH_REQUIRED',
  CsrfValidationFailed: 'CSRF_VALIDATION_FAILED',
  InvalidCredentials: 'INVALID_CREDENTIALS',
  AccountLocked: 'ACCOUNT_LOCKED',
  RateLimited: 'RATE_LIMITED',
  NotFound: 'NOT_FOUND',
  InternalError: 'INTERNAL_ERROR',
  LastAdminProtected: 'LAST_ADMIN_PROTECTED',
  AuthModeAlreadyMultiuser: 'AUTH_MODE_ALREADY_MULTIUSER',
  PasswordChangeRequired: 'PASSWORD_CHANGE_REQUIRED',
  Forbidden: 'FORBIDDEN',
  IpNotAllowed: 'IP_NOT_ALLOWED',
  AuthModeDowngradeNotSupported: 'AUTH_MODE_DOWNGRADE_NOT_SUPPORTED',
  // M2 reference-data codes (docs/API.md §22).
  DepartmentInUse: 'DEPARTMENT_IN_USE',
  UnitExists: 'UNIT_EXISTS',
  AliasExists: 'ALIAS_EXISTS',
  UnitInUse: 'UNIT_IN_USE',
  IngredientExists: 'INGREDIENT_EXISTS',
  IngredientInUse: 'INGREDIENT_IN_USE',
  InvalidUnitDimensionChange: 'INVALID_UNIT_DIMENSION_CHANGE',
  ProteinExists: 'PROTEIN_EXISTS',
  ProteinInUse: 'PROTEIN_IN_USE',
  TagExists: 'TAG_EXISTS',
  TagInUse: 'TAG_IN_USE',
  // M3 recipe codes (docs/API.md §23).
  RecipeExists: 'RECIPE_EXISTS',
  // M4 import codes (docs/API.md §15). Consumption lands with the M4-08 review screen.
  ImportUrlInvalid: 'IMPORT_URL_INVALID',
  ImportUrlBlocked: 'IMPORT_URL_BLOCKED',
  ImportFetchFailed: 'IMPORT_FETCH_FAILED',
  ImportUnsupportedContent: 'IMPORT_UNSUPPORTED_CONTENT',
  ImportTooLarge: 'IMPORT_TOO_LARGE',
  ImportNoRecipe: 'IMPORT_NO_RECIPE',
  ImportRateLimited: 'IMPORT_RATE_LIMITED',
  // M5 calendar codes (docs/M5_PLAN.md §1.12).
  CalendarDateInPast: 'CALENDAR_DATE_IN_PAST',
  CalendarBlankCannotLock: 'CALENDAR_BLANK_CANNOT_LOCK',
  CalendarEntryLocked: 'CALENDAR_ENTRY_LOCKED',
  CalendarRecipeRequired: 'CALENDAR_RECIPE_REQUIRED',
  CalendarMiscLabelRequired: 'CALENDAR_MISC_LABEL_REQUIRED',
  // M12-06 historical-correction codes (docs/API.md §7, §17).
  CalendarDateNotPast: 'CALENDAR_DATE_NOT_PAST',
  CalendarDateLocked: 'CALENDAR_DATE_LOCKED',
  // M12-05/M12-11 dangerous-action guard codes (docs/API.md §4).
  ReasonRequired: 'REASON_REQUIRED',
  ReasonTooLong: 'REASON_TOO_LONG',
  ReasonInvalid: 'REASON_INVALID',
  ConfirmationMismatch: 'CONFIRMATION_MISMATCH',
  // M6 randomizer/undo codes (docs/API.md §7).
  RandomizerNotApplicable: 'RANDOMIZER_NOT_APPLICABLE',
  RandomizerNoCandidates: 'RANDOMIZER_NO_CANDIDATES',
  UndoConflict: 'UNDO_CONFLICT',
  UndoExpired: 'UNDO_EXPIRED',
  // M7 batch reroll codes (docs/API.md §7).
  BatchConflict: 'BATCH_CONFLICT',
  BatchDraftExpired: 'BATCH_DRAFT_EXPIRED',
  // M8 grocery codes (docs/API.md §13).
  GroceryRangeTooLarge: 'GROCERY_RANGE_TOO_LARGE',
  GroceryStapleExists: 'GROCERY_STAPLE_EXISTS',
  // M13 backup/restore codes (docs/API.md §4, §18, §21).
  BackupKeyMissing: 'BACKUP_KEY_MISSING',
  BackupKeyMismatch: 'BACKUP_KEY_MISMATCH',
  BackupIntegrityFailed: 'BACKUP_INTEGRITY_FAILED',
  BackupFormatUnsupported: 'BACKUP_FORMAT_UNSUPPORTED',
  BackupSchemaTooNew: 'BACKUP_SCHEMA_TOO_NEW',
  RestoreInProgress: 'RESTORE_IN_PROGRESS',
  BackupClientTooOld: 'BACKUP_CLIENT_TOO_OLD',
  BackupInProgress: 'BACKUP_IN_PROGRESS',
  BackupFailed: 'BACKUP_FAILED',
  BackupNotFound: 'BACKUP_NOT_FOUND',
  BackupFileMissing: 'BACKUP_FILE_MISSING',
  RestoreFailed: 'RESTORE_FAILED',
} as const

export type ErrorCode = (typeof ErrorCodes)[keyof typeof ErrorCodes]

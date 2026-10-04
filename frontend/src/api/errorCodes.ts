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
} as const

export type ErrorCode = (typeof ErrorCodes)[keyof typeof ErrorCodes]

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import { formatTimestamp } from '../../lib/dateFormat'

function firstFieldError(details: Record<string, unknown>): string | null {
  const fieldErrors = details.field_errors
  if (typeof fieldErrors !== 'object' || fieldErrors === null) {
    return null
  }
  for (const messages of Object.values(fieldErrors as Record<string, unknown>)) {
    if (Array.isArray(messages) && typeof messages[0] === 'string' && messages[0].length > 0) {
      return messages[0]
    }
  }
  return null
}

function lockedUntilSuffix(details: Record<string, unknown>): string {
  const lockedUntil = details.locked_until
  if (typeof lockedUntil !== 'string') {
    return ''
  }
  // `formatTimestamp` returns its input verbatim when it cannot parse, so an
  // unreadable value contributes no suffix rather than a bogus time.
  const formatted = formatTimestamp(lockedUntil)
  return formatted === lockedUntil ? '' : ` until ${formatted}`
}

/** Human-readable login failures; the backend stays the source of truth. */
export function loginErrorMessage(error: unknown): string {
  if (isApiError(error)) {
    switch (error.code) {
      case ErrorCodes.InvalidCredentials:
        return 'The username or password is incorrect.'
      case ErrorCodes.AccountLocked:
        return `This account is temporarily locked${lockedUntilSuffix(error.details)}. Try again later.`
      case ErrorCodes.RateLimited:
        return 'Too many sign-in attempts. Wait a few minutes and try again.'
      case ErrorCodes.ValidationFailed:
        return firstFieldError(error.details) ?? 'Enter both your username and password.'
    }
  }
  return 'Sign-in failed. Check the connection and try again.'
}

/** Human-readable change-password failures. */
export function changePasswordErrorMessage(error: unknown): string {
  if (isApiError(error)) {
    switch (error.code) {
      case ErrorCodes.InvalidCredentials:
        return 'The current password is incorrect.'
      case ErrorCodes.ValidationFailed:
        return (
          firstFieldError(error.details) ??
          'The new password must be 12–128 characters and differ from the current password.'
        )
      case ErrorCodes.PasswordChangeRequired:
        return 'Set a new password before continuing.'
    }
  }
  return 'Password change failed. Check the connection and try again.'
}

/**
 * Field-level validation shared by the setup wizard and the admin console.
 * These rules mirror the server (`HouseholdSettingsPatchValidator`,
 * `UsernamePolicy`, `PasswordPolicy`); the server stays authoritative.
 */

export const HOUSEHOLD_NAME_MAX_LENGTH = 100
export const MIN_DINER_COUNT = 1
export const MAX_DINER_COUNT = 20
export const USERNAME_MIN_LENGTH = 3
export const USERNAME_MAX_LENGTH = 32
export const PASSWORD_MIN_LENGTH = 12
export const PASSWORD_MAX_LENGTH = 128

export const USERNAME_PATTERN = /^[A-Za-z0-9._-]+$/

/** Mirrors the server rule: no C0 controls and no DEL in display names. */
export function containsControlCharacters(value: string): boolean {
  for (const character of value) {
    const codePoint = character.codePointAt(0) ?? 0
    if (codePoint < 0x20 || codePoint === 0x7f) {
      return true
    }
  }
  return false
}

export function validateHouseholdName(value: string): string | undefined {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return 'Enter a household name.'
  }
  if (containsControlCharacters(trimmed)) {
    return 'The household name must not contain control characters.'
  }
  if (trimmed.length > HOUSEHOLD_NAME_MAX_LENGTH) {
    return `Use at most ${HOUSEHOLD_NAME_MAX_LENGTH} characters.`
  }
  return undefined
}

export function validateDinerCount(value: string): string | undefined {
  const trimmed = value.trim()
  if (!/^\d+$/.test(trimmed)) {
    return 'Enter a whole number of diners.'
  }
  const count = Number(trimmed)
  if (count < MIN_DINER_COUNT || count > MAX_DINER_COUNT) {
    return `Enter a number between ${MIN_DINER_COUNT} and ${MAX_DINER_COUNT}.`
  }
  return undefined
}

export function validateAdminUsername(value: string): string | undefined {
  const trimmed = value.trim()
  if (
    trimmed.length < USERNAME_MIN_LENGTH ||
    trimmed.length > USERNAME_MAX_LENGTH ||
    !USERNAME_PATTERN.test(trimmed)
  ) {
    return `Use ${USERNAME_MIN_LENGTH}–${USERNAME_MAX_LENGTH} characters: letters, digits, ".", "_" or "-".`
  }
  return undefined
}

export function validateAdminPassword(value: string): string | undefined {
  if (value.length < PASSWORD_MIN_LENGTH || value.length > PASSWORD_MAX_LENGTH) {
    return `Use ${PASSWORD_MIN_LENGTH}–${PASSWORD_MAX_LENGTH} characters.`
  }
  return undefined
}

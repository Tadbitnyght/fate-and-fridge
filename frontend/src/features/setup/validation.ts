import type { AuthMode } from '../../api/auth'
import type { CompleteSetupRequest, StartupPage } from '../../api/setup'

/** Editable wizard state; all values are kept as strings where inputs are free-form. */
export interface SetupFormValues {
  authMode: AuthMode | null
  householdName: string
  timezone: string
  weekStartsOn: number
  defaultDinerCount: string
  startupPage: StartupPage
  adminUsername: string
  adminPassword: string
  adminPasswordConfirm: string
  allowlistEnabled: boolean
  allowlistRangesText: string
  updateCheckEnabled: boolean
}

export type SetupStep = 'mode' | 'household' | 'admin' | 'allowlist' | 'review' | 'recovery'

export type FieldErrors = Record<string, string>

export interface ValidationContext {
  /** Supported IANA zones, or null when the runtime cannot enumerate them. */
  timezoneOptions: string[] | null
}

export const HOUSEHOLD_NAME_MAX_LENGTH = 100
export const MIN_DINER_COUNT = 1
export const MAX_DINER_COUNT = 20
export const USERNAME_MIN_LENGTH = 3
export const USERNAME_MAX_LENGTH = 32
export const PASSWORD_MIN_LENGTH = 12
export const PASSWORD_MAX_LENGTH = 128

const USERNAME_PATTERN = /^[A-Za-z0-9._-]+$/

/** Mirrors the server rule: no C0 controls and no DEL in display names. */
function containsControlCharacters(value: string): boolean {
  for (const character of value) {
    const codePoint = character.codePointAt(0) ?? 0
    if (codePoint < 0x20 || codePoint === 0x7f) {
      return true
    }
  }
  return false
}

/** True when the runtime already considers this the selected zone (aliases such as UTC). */
function isDetectedTimeZone(value: string): boolean {
  try {
    const detected = Intl.DateTimeFormat().resolvedOptions().timeZone
    return typeof detected === 'string' && detected.toLowerCase() === value.toLowerCase()
  } catch {
    return false
  }
}

const STEP_ORDER: SetupStep[] = ['mode', 'household', 'admin', 'allowlist', 'review']

const STEP_FIELDS: Record<SetupStep, string[]> = {
  mode: ['auth_mode'],
  household: [
    'household_name',
    'timezone',
    'week_starts_on',
    'default_diner_count',
    'startup_page',
  ],
  admin: ['admin', 'admin.username', 'admin.password', 'admin.password_confirm'],
  allowlist: ['ip_allowlist', 'ip_allowlist.enabled', 'ip_allowlist.ranges'],
  review: [],
  recovery: [],
}

/** Steps for the current mode: the Admin step exists only for multiuser. */
export function wizardSteps(authMode: AuthMode | null): SetupStep[] {
  return authMode === 'multiuser'
    ? ['mode', 'household', 'admin', 'allowlist', 'review']
    : ['mode', 'household', 'allowlist', 'review']
}

export function nextStep(steps: SetupStep[], current: SetupStep): SetupStep {
  const index = steps.indexOf(current)
  return steps[Math.min(index + 1, steps.length - 1)]
}

export function previousStep(steps: SetupStep[], current: SetupStep): SetupStep {
  const index = steps.indexOf(current)
  return steps[Math.max(index - 1, 0)]
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

export function validateTimezone(value: string, options: string[] | null): string | undefined {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return 'Choose a timezone.'
  }
  if (options === null) {
    return undefined
  }
  if (options.some((zone) => zone.toLowerCase() === trimmed.toLowerCase())) {
    return undefined
  }
  // `Intl.supportedValuesOf` omits aliases such as UTC even though the server
  // accepts them; the detected browser zone is always allowed.
  if (isDetectedTimeZone(trimmed)) {
    return undefined
  }
  return 'Choose a valid IANA timezone, for example America/Chicago.'
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

/** Strict IPv4 octets: no leading zeros (`010.0.0.1` is rejected like the server). */
function parseIPv4Octets(value: string): number[] | null {
  const parts = value.split('.')
  if (parts.length !== 4) {
    return null
  }
  const octets: number[] = []
  for (const part of parts) {
    if (!/^(0|[1-9]\d{0,2})$/.test(part)) {
      return null
    }
    const octet = Number(part)
    if (octet > 255) {
      return null
    }
    octets.push(octet)
  }
  return octets
}

function parseHexGroups(value: string): number[] | null {
  if (value.length === 0) {
    return []
  }
  const groups: number[] = []
  for (const part of value.split(':')) {
    if (!/^[0-9a-fA-F]{1,4}$/.test(part)) {
      return null
    }
    groups.push(parseInt(part, 16))
  }
  return groups
}

/**
 * Expands an IPv6 address (including `::` and an embedded dotted quad) into its
 * 16 bytes, so mapped forms can be detected regardless of how they are written.
 */
function parseIPv6Bytes(value: string): number[] | null {
  if (!value.includes(':')) {
    return null
  }

  let head = value
  let tailGroups: number[] = []
  const lastColon = value.lastIndexOf(':')
  const tail = value.slice(lastColon + 1)
  if (tail.includes('.')) {
    const octets = parseIPv4Octets(tail)
    if (octets === null) {
      return null
    }
    head = value.slice(0, lastColon)
    tailGroups = [(octets[0] << 8) | octets[1], (octets[2] << 8) | octets[3]]
  }

  const doubleColon = head.indexOf('::')
  let groups: number[] | null
  if (doubleColon === -1) {
    const parsed = parseHexGroups(head)
    groups = parsed === null ? null : [...parsed, ...tailGroups]
  } else {
    const left = parseHexGroups(head.slice(0, doubleColon))
    const right = parseHexGroups(head.slice(doubleColon + 2))
    if (left === null || right === null) {
      return null
    }
    const missing = 8 - (left.length + right.length + tailGroups.length)
    if (missing < 1) {
      return null
    }
    groups = [...left, ...Array<number>(missing).fill(0), ...right, ...tailGroups]
  }

  if (groups === null || groups.length !== 8) {
    return null
  }

  const bytes: number[] = []
  for (const group of groups) {
    bytes.push((group >> 8) & 0xff, group & 0xff)
  }
  return bytes
}

function isIPv4(value: string): boolean {
  return parseIPv4Octets(value) !== null
}

function isIPv6(value: string): boolean {
  return parseIPv6Bytes(value) !== null
}

/** True for `::ffff:a.b.c.d` and long forms such as `0:0:0:0:0:ffff:1.2.3.4`. */
function isIPv4Mapped(value: string): boolean {
  const bytes = parseIPv6Bytes(value)
  if (bytes === null) {
    return false
  }
  return bytes.slice(0, 10).every((byte) => byte === 0) && bytes[10] === 0xff && bytes[11] === 0xff
}

/** Validates one bare address or CIDR range; returns a problem message or undefined. */
export function validateAllowlistRange(value: string): string | undefined {
  const slash = value.indexOf('/')
  const address = (slash === -1 ? value : value.slice(0, slash)).trim()
  const prefixText = slash === -1 ? null : value.slice(slash + 1)

  if (address.length === 0 || address.includes('%') || address.includes(' ')) {
    return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
  }

  let prefix: number | null = null
  if (prefixText !== null) {
    // No leading zeros: `1.2.3.4/032` is rejected like the server.
    if (!/^(0|[1-9]\d{0,2})$/.test(prefixText)) {
      return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
    }
    prefix = Number(prefixText)
  }

  if (isIPv4(address)) {
    if (prefix !== null && prefix > 32) {
      return 'An IPv4 prefix must be between 0 and 32.'
    }
    return undefined
  }

  if (isIPv6(address)) {
    if (prefix !== null && prefix > 128) {
      return 'An IPv6 prefix must be between 0 and 128.'
    }
    if (prefix !== null && prefix < 96 && isIPv4Mapped(address)) {
      return 'An IPv4-mapped IPv6 range must use a prefix of /96 or longer.'
    }
    return undefined
  }

  return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
}

/** Splits the textarea into trimmed non-empty lines and validates each one. */
export function validateAllowlistText(text: string): { ranges: string[]; errors: string[] } {
  const ranges: string[] = []
  const errors: string[] = []

  text.split(/\r?\n/).forEach((raw, index) => {
    const line = raw.trim()
    if (line.length === 0) {
      return
    }
    const problem = validateAllowlistRange(line)
    if (problem) {
      errors.push(`Line ${index + 1}: ${problem}`)
    } else {
      ranges.push(line)
    }
  })

  return { ranges, errors }
}

function validateHousehold(values: SetupFormValues, context: ValidationContext): FieldErrors {
  const errors: FieldErrors = {}
  const householdName = validateHouseholdName(values.householdName)
  if (householdName) {
    errors['household_name'] = householdName
  }
  const timezone = validateTimezone(values.timezone, context.timezoneOptions)
  if (timezone) {
    errors['timezone'] = timezone
  }
  if (
    !Number.isInteger(values.weekStartsOn) ||
    values.weekStartsOn < 0 ||
    values.weekStartsOn > 6
  ) {
    errors['week_starts_on'] = 'Choose a day between Sunday and Saturday.'
  }
  const dinerCount = validateDinerCount(values.defaultDinerCount)
  if (dinerCount) {
    errors['default_diner_count'] = dinerCount
  }
  if (values.startupPage !== 'calendar' && values.startupPage !== 'dashboard') {
    errors['startup_page'] = 'Choose Calendar or Dashboard.'
  }
  return errors
}

function validateAdmin(values: SetupFormValues): FieldErrors {
  const errors: FieldErrors = {}
  const username = validateAdminUsername(values.adminUsername)
  if (username) {
    errors['admin.username'] = username
  }
  const password = validateAdminPassword(values.adminPassword)
  if (password) {
    errors['admin.password'] = password
  }
  if (values.adminPasswordConfirm !== values.adminPassword) {
    errors['admin.password_confirm'] = 'The passwords do not match.'
  }
  return errors
}

function validateAllowlist(values: SetupFormValues): FieldErrors {
  const errors: FieldErrors = {}
  if (!values.allowlistEnabled) {
    return errors
  }
  const { ranges, errors: lineErrors } = validateAllowlistText(values.allowlistRangesText)
  if (lineErrors.length > 0) {
    errors['ip_allowlist.ranges'] = lineErrors.join(' ')
  } else if (ranges.length === 0) {
    errors['ip_allowlist.ranges'] =
      'Add at least one IP address or CIDR range when the allowlist is enabled.'
  }
  return errors
}

export function validateStep(
  step: SetupStep,
  values: SetupFormValues,
  context: ValidationContext,
): FieldErrors {
  switch (step) {
    case 'mode':
      return values.authMode === 'multiuser' || values.authMode === 'single_user'
        ? {}
        : { auth_mode: 'Choose an installation mode.' }
    case 'household':
      return validateHousehold(values, context)
    case 'admin':
      return values.authMode === 'multiuser' ? validateAdmin(values) : {}
    case 'allowlist':
      return validateAllowlist(values)
    case 'review':
    case 'recovery':
      return {}
  }
}

/** Full validation used before submitting (all steps). */
export function validateAll(values: SetupFormValues, context: ValidationContext): FieldErrors {
  return {
    ...validateStep('mode', values, context),
    ...validateStep('household', values, context),
    ...validateStep('admin', values, context),
    ...validateStep('allowlist', values, context),
  }
}

/** Maps server `details.field_errors` (or client errors) to the step that shows them. */
export function stepForFieldErrors(errors: FieldErrors): SetupStep {
  for (const step of STEP_ORDER) {
    if (STEP_FIELDS[step].some((field) => Object.hasOwn(errors, field))) {
      return step
    }
  }
  return 'review'
}

const FORM_FIELD_ERROR_KEYS: Partial<Record<keyof SetupFormValues, string[]>> = {
  authMode: ['auth_mode'],
  householdName: ['household_name'],
  timezone: ['timezone'],
  weekStartsOn: ['week_starts_on'],
  defaultDinerCount: ['default_diner_count'],
  startupPage: ['startup_page'],
  adminUsername: ['admin.username', 'admin'],
  adminPassword: ['admin.password', 'admin'],
  adminPasswordConfirm: ['admin.password_confirm'],
  allowlistEnabled: ['ip_allowlist.enabled', 'ip_allowlist.ranges', 'ip_allowlist'],
  allowlistRangesText: ['ip_allowlist.ranges'],
  updateCheckEnabled: ['update_check_enabled'],
}

/** Drops the messages that belong to one form field (used while the user edits). */
export function clearFieldError(field: keyof SetupFormValues, errors: FieldErrors): FieldErrors {
  const keys = FORM_FIELD_ERROR_KEYS[field]
  if (!keys) {
    return errors
  }
  const next: FieldErrors = {}
  for (const [key, message] of Object.entries(errors)) {
    if (!keys.includes(key)) {
      next[key] = message
    }
  }
  return next
}

/** Drops only the messages owned by the given step (leaves other steps' errors intact). */
export function clearStepErrors(step: SetupStep, errors: FieldErrors): FieldErrors {
  const fields = STEP_FIELDS[step]
  const next: FieldErrors = {}
  for (const [key, message] of Object.entries(errors)) {
    if (!fields.includes(key)) {
      next[key] = message
    }
  }
  return next
}

/** Extracts the first message per field from a server error envelope. */
export function extractServerFieldErrors(details: Record<string, unknown>): FieldErrors {
  const raw = details.field_errors
  const errors: FieldErrors = {}
  if (typeof raw !== 'object' || raw === null) {
    return errors
  }
  for (const [field, messages] of Object.entries(raw as Record<string, unknown>)) {
    if (Array.isArray(messages) && typeof messages[0] === 'string' && messages[0].length > 0) {
      errors[field] = messages[0]
    }
  }
  return errors
}

/** Builds the `POST /setup/complete` body from the wizard state. */
export function buildRequest(values: SetupFormValues): CompleteSetupRequest {
  if (values.authMode === null) {
    throw new Error('Choose an installation mode before submitting setup.')
  }

  const request: CompleteSetupRequest = {
    household_name: values.householdName.trim(),
    timezone: values.timezone.trim(),
    week_starts_on: values.weekStartsOn,
    default_diner_count: Number(values.defaultDinerCount),
    startup_page: values.startupPage,
    auth_mode: values.authMode,
    ip_allowlist: {
      enabled: values.allowlistEnabled,
      ranges: validateAllowlistText(values.allowlistRangesText).ranges,
    },
    update_check_enabled: values.updateCheckEnabled,
  }

  if (values.authMode === 'multiuser') {
    request.admin = {
      username: values.adminUsername.trim(),
      password: values.adminPassword,
    }
  }

  return request
}

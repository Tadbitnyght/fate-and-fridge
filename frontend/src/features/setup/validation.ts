import type { AuthMode } from '../../api/auth'
import type { CompleteSetupRequest, StartupPage } from '../../api/setup'
import type { FieldErrors } from '../../utils/fieldErrors'
import { validateAllowlistText } from '../../utils/ipRanges'
import { validateTimezone } from '../../utils/timezones'
import {
  validateAdminPassword,
  validateAdminUsername,
  validateDinerCount,
  validateHouseholdName,
} from '../../utils/validation'

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

export interface ValidationContext {
  /** Supported IANA zones, or null when the runtime cannot enumerate them. */
  timezoneOptions: string[] | null
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

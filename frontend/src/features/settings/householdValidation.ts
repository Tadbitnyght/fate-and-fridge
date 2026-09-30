import type { StartupPage } from '../../api/setup'
import type { FieldErrors } from '../../utils/fieldErrors'
import { validateTimezone } from '../../utils/timezones'
import { validateDinerCount, validateHouseholdName } from '../../utils/validation'

/** Editable household settings form state (numbers kept as strings while typing). */
export interface HouseholdFormValues {
  householdName: string
  timezone: string
  weekStartsOn: number
  defaultDinerCount: string
  startupPage: StartupPage
}

export const HOUSEHOLD_FORM_ERROR_KEYS: Record<keyof HouseholdFormValues, string[]> = {
  householdName: ['household_name'],
  timezone: ['timezone'],
  weekStartsOn: ['week_starts_on'],
  defaultDinerCount: ['default_diner_count'],
  startupPage: ['startup_page'],
}

export function validateHouseholdForm(
  values: HouseholdFormValues,
  timezoneOptions: string[] | null,
): FieldErrors {
  const errors: FieldErrors = {}
  const name = validateHouseholdName(values.householdName)
  if (name) {
    errors['household_name'] = name
  }
  const timezone = validateTimezone(values.timezone, timezoneOptions)
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
  const diners = validateDinerCount(values.defaultDinerCount)
  if (diners) {
    errors['default_diner_count'] = diners
  }
  if (values.startupPage !== 'calendar' && values.startupPage !== 'dashboard') {
    errors['startup_page'] = 'Choose Calendar or Dashboard.'
  }
  return errors
}

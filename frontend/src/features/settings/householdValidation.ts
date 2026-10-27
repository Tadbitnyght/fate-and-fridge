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
  randomizerRepeatLookbackDays: string
  randomizerRepeatLookaheadDays: string
  randomizerRepeatMultiplier: string
  randomizerSpecialDayMultiplier: string
  randomizerProteinMultiplier: string
  randomizerProteinNeighborDistance: string
  randomizerFavoriteMultiplier: string
  randomizerSpacingNearDays: string
  randomizerSpacingFarDays: string
  randomizerSpacingSearchDays: string
}

export const HOUSEHOLD_FORM_ERROR_KEYS: Record<keyof HouseholdFormValues, string[]> = {
  householdName: ['household_name'],
  timezone: ['timezone'],
  weekStartsOn: ['week_starts_on'],
  defaultDinerCount: ['default_diner_count'],
  startupPage: ['startup_page'],
  randomizerRepeatLookbackDays: ['randomizer_repeat_lookback_days'],
  randomizerRepeatLookaheadDays: ['randomizer_repeat_lookahead_days'],
  randomizerRepeatMultiplier: ['randomizer_repeat_multiplier'],
  randomizerSpecialDayMultiplier: ['randomizer_special_day_multiplier'],
  randomizerProteinMultiplier: ['randomizer_protein_multiplier'],
  randomizerProteinNeighborDistance: ['randomizer_protein_neighbor_distance'],
  randomizerFavoriteMultiplier: ['randomizer_favorite_multiplier'],
  randomizerSpacingNearDays: ['randomizer_spacing_near_days'],
  randomizerSpacingFarDays: ['randomizer_spacing_far_days'],
  randomizerSpacingSearchDays: ['randomizer_spacing_search_days'],
}

type RandomizerFormField =
  | 'randomizerRepeatLookbackDays'
  | 'randomizerRepeatLookaheadDays'
  | 'randomizerRepeatMultiplier'
  | 'randomizerSpecialDayMultiplier'
  | 'randomizerProteinMultiplier'
  | 'randomizerProteinNeighborDistance'
  | 'randomizerFavoriteMultiplier'
  | 'randomizerSpacingNearDays'
  | 'randomizerSpacingFarDays'
  | 'randomizerSpacingSearchDays'

interface RandomizerRangeRule {
  field: RandomizerFormField
  /** snake_case API field, used as the server-compatible error key. */
  apiKey: string
  label: string
  min: number
  max: number
  /** Rendered bounds, so ranges keep their documented precision (e.g. "1.00"). */
  minText: string
  maxText: string
  integer: boolean
}

/** Client mirror of the server randomizer ranges (docs/API.md §21, M6_PLAN §1.3). */
const RANDOMIZER_RANGE_RULES: readonly RandomizerRangeRule[] = [
  {
    field: 'randomizerRepeatLookbackDays',
    apiKey: 'randomizer_repeat_lookback_days',
    label: 'Repeat lookback (days)',
    min: 0,
    max: 60,
    minText: '0',
    maxText: '60',
    integer: true,
  },
  {
    field: 'randomizerRepeatLookaheadDays',
    apiKey: 'randomizer_repeat_lookahead_days',
    label: 'Repeat lookahead (days)',
    min: 0,
    max: 60,
    minText: '0',
    maxText: '60',
    integer: true,
  },
  {
    field: 'randomizerRepeatMultiplier',
    apiKey: 'randomizer_repeat_multiplier',
    label: 'Repeat multiplier',
    min: 0.01,
    max: 1,
    minText: '0.01',
    maxText: '1.00',
    integer: false,
  },
  {
    field: 'randomizerSpecialDayMultiplier',
    apiKey: 'randomizer_special_day_multiplier',
    label: 'Special-day multiplier',
    min: 0,
    max: 1,
    minText: '0.00',
    maxText: '1.00',
    integer: false,
  },
  {
    field: 'randomizerProteinMultiplier',
    apiKey: 'randomizer_protein_multiplier',
    label: 'Protein multiplier',
    min: 0.1,
    max: 1,
    minText: '0.10',
    maxText: '1.00',
    integer: false,
  },
  {
    field: 'randomizerProteinNeighborDistance',
    apiKey: 'randomizer_protein_neighbor_distance',
    label: 'Protein neighbor distance',
    min: 1,
    max: 7,
    minText: '1',
    maxText: '7',
    integer: true,
  },
  {
    field: 'randomizerFavoriteMultiplier',
    apiKey: 'randomizer_favorite_multiplier',
    label: 'Favorite multiplier',
    min: 1,
    max: 2,
    minText: '1.00',
    maxText: '2.00',
    integer: false,
  },
  {
    field: 'randomizerSpacingNearDays',
    apiKey: 'randomizer_spacing_near_days',
    label: 'Spacing near (days)',
    min: 1,
    max: 30,
    minText: '1',
    maxText: '30',
    integer: true,
  },
  {
    field: 'randomizerSpacingFarDays',
    apiKey: 'randomizer_spacing_far_days',
    label: 'Spacing far (days)',
    min: 1,
    max: 60,
    minText: '1',
    maxText: '60',
    integer: true,
  },
  {
    field: 'randomizerSpacingSearchDays',
    apiKey: 'randomizer_spacing_search_days',
    label: 'Spacing search (days)',
    min: 7,
    max: 365,
    minText: '7',
    maxText: '365',
    integer: true,
  },
]

function rangeMessage(rule: RandomizerRangeRule): string {
  const kind = rule.integer ? 'a whole number' : 'a number'
  return `${rule.label} must be ${kind} between ${rule.minText} and ${rule.maxText}.`
}

function validateRandomizerRange(value: string, rule: RandomizerRangeRule): string | undefined {
  const trimmed = value.trim()
  const pattern = rule.integer ? /^\d+$/ : /^\d+(\.\d+)?$/
  if (!pattern.test(trimmed)) {
    return rangeMessage(rule)
  }
  const parsed = Number(trimmed)
  if (parsed < rule.min || parsed > rule.max) {
    return rangeMessage(rule)
  }
  return undefined
}

function parseWholeNumber(value: string): number | null {
  const trimmed = value.trim()
  return /^\d+$/.test(trimmed) ? Number(trimmed) : null
}

/** Cross-field rule: spacing far must be at least spacing near (docs/API.md §21). */
function validateSpacingOrder(values: HouseholdFormValues): string | undefined {
  const near = parseWholeNumber(values.randomizerSpacingNearDays)
  const far = parseWholeNumber(values.randomizerSpacingFarDays)
  if (near === null || far === null || far >= near) {
    return undefined
  }
  return 'Spacing far days must be greater than or equal to spacing near days.'
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
  for (const rule of RANDOMIZER_RANGE_RULES) {
    const message = validateRandomizerRange(values[rule.field], rule)
    if (message) {
      errors[rule.apiKey] = message
    }
  }
  // The server evaluates the cross-field rule only after every per-field range
  // passes, so mirror that: skip it while either spacing field has an error.
  if (
    errors['randomizer_spacing_near_days'] === undefined &&
    errors['randomizer_spacing_far_days'] === undefined
  ) {
    const spacingOrder = validateSpacingOrder(values)
    if (spacingOrder) {
      errors['randomizer_spacing_far_days'] = spacingOrder
    }
  }
  return errors
}

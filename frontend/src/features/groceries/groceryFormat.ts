import { isApiError } from '../../api/client'
import type { GroceryDepartment } from '../../api/groceries'
import { ErrorCodes } from '../../api/errorCodes'
import { formatClockTime, formatHouseholdDate, formatLocalDate } from '../../lib/dateFormat'
import { parseIsoDate } from '../calendar/calendarDates'
import { firstFieldError, extractServerFieldErrors } from '../../utils/fieldErrors'

const WEEKDAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** Short meal attribution, e.g. `Tue 10/6` (docs/M8_PLAN.md §1.6). */
export function formatSourceDate(iso: string): string {
  const date = parseIsoDate(iso)
  return `${WEEKDAY_NAMES[date.getDay()]} ${date.getMonth() + 1}/${date.getDate()}`
}

/**
 * Departments arrive in server order (ascending `sort_order`, then name) with
 * the null-department "Other" group last (docs/API.md §13). This re-asserts the
 * "Other last" guarantee defensively without disturbing the relative order of
 * the remaining departments (a stable sort keeps server order intact).
 */
export function orderDepartments(departments: GroceryDepartment[]): GroceryDepartment[] {
  return [...departments].sort((left, right) => Number(left.is_other) - Number(right.is_other))
}

/**
 * Readable range summary, e.g. `10-10-26 – 10-16-26`. A single-day range
 * collapses to one date. Both ends go through the shared product date formatter.
 */
export function formatRangeSummary(start: string, end: string): string {
  const startLabel = formatHouseholdDate(start)
  return start === end ? startLabel : `${startLabel} – ${formatHouseholdDate(end)}`
}

/**
 * Quantity/unit text for a grocery line, e.g. `1 1/2 lb` (docs/M8_PLAN.md §1.5).
 * A quantity-less line renders only its unit, or nothing when it has neither.
 */
export function formatQuantityLabel(quantityText: string, unit: string | null): string {
  if (quantityText.length > 0) {
    return unit !== null ? `${quantityText} ${unit}` : quantityText
  }
  return unit ?? ''
}

/**
 * Print-view "generated at" stamp, e.g. `10-12-26 3:04 PM`. Built by hand from
 * the local clock so it is deterministic under invariant globalization (no
 * `toLocaleString`, see `AGENTS.md`); the date itself is the shared product
 * format.
 */
export function formatGeneratedAt(date: Date): string {
  return `${formatLocalDate(date)} ${formatClockTime(date.getHours(), date.getMinutes())}`
}

/**
 * Maps a grocery error envelope to a clear, actionable message (docs/API.md
 * §13). Never returns a raw server message as the only affordance: known codes
 * get a purpose-built sentence.
 */
export function describeGroceryError(error: unknown, fallback: string): string {
  if (!isApiError(error)) {
    return fallback
  }
  switch (error.code) {
    case ErrorCodes.Forbidden:
      return 'You do not have permission to do that. Ask an Admin for the groceries.edit permission.'
    case ErrorCodes.GroceryRangeTooLarge:
      return 'That range is too long. Choose 366 days or fewer.'
    case ErrorCodes.GroceryStapleExists: {
      const existing = error.details['existing_id']
      return typeof existing === 'number'
        ? `A staple with this name already exists (#${existing}).`
        : 'A staple with this name already exists.'
    }
    case ErrorCodes.NotFound:
      return 'That item no longer exists. Refresh the list and try again.'
    case ErrorCodes.ValidationFailed: {
      const fieldError = firstFieldError(extractServerFieldErrors(error.details))
      return fieldError ?? error.message
    }
    default:
      return error.message
  }
}

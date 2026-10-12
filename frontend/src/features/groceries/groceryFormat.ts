import { isApiError } from '../../api/client'
import type { GroceryDepartment } from '../../api/groceries'
import { ErrorCodes } from '../../api/errorCodes'
import { parseIsoDate } from '../calendar/calendarDates'
import { firstFieldError, extractServerFieldErrors } from '../../utils/fieldErrors'

const WEEKDAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const MONTH_NAMES = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
]

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

/** Readable range summary, e.g. `Oct 10 – Oct 16`. */
export function formatRangeSummary(start: string, end: string): string {
  const startDate = parseIsoDate(start)
  const endDate = parseIsoDate(end)
  const startLabel = `${MONTH_NAMES[startDate.getMonth()]} ${startDate.getDate()}`
  const endLabel = `${MONTH_NAMES[endDate.getMonth()]} ${endDate.getDate()}`
  return start === end ? startLabel : `${startLabel} – ${endLabel}`
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

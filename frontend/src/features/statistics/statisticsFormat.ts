/**
 * Household-local date labels shared by the statistics overview (M11-04) and
 * the meal-history browser (M11-05, docs/M11_PLAN.md §1.26).
 *
 * Household-local dates reach the client as bare `YYYY-MM-DD` strings. They are
 * labelled straight from that string — never through `new Date(value)`, which
 * parses a bare date as UTC midnight and can shift the day in a negative-offset
 * timezone, and never through `toLocale*`, which is culture-dependent and a
 * silent no-op under this workstation's invariant globalization.
 */

/** English month abbreviations, indexed from January (0). */
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/**
 * Renders a household-local `YYYY-MM-DD` string as `Mon D, YYYY`. A value that
 * is not a bare date (or has an impossible month) is shown verbatim rather than
 * guessed at.
 */
export function formatHouseholdDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (match === null) {
    return value
  }
  const month = MONTHS[Number(match[2]) - 1]
  if (month === undefined) {
    return value
  }
  return `${month} ${Number(match[3])}, ${match[1]}`
}

/**
 * Renders a household-local `YYYY-MM` month key (a paging cursor) as `Mon
 * YYYY`. Anything else is shown verbatim.
 */
export function formatHouseholdMonth(key: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(key)
  if (match === null) {
    return key
  }
  const month = MONTHS[Number(match[2]) - 1]
  if (month === undefined) {
    return key
  }
  return `${month} ${match[1]}`
}

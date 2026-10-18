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

/** Days in each month by January-based index, ignoring leap years. */
const DAYS_IN_MONTH = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]

/** Gregorian leap-year rule, table-driven so it never depends on ICU/Date. */
function isLeapYear(year: number): boolean {
  return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0
}

/**
 * Renders a household-local `YYYY-MM-DD` string as `Mon D, YYYY`. A value that
 * is not a bare date, or that carries an impossible month or day (including an
 * impossible day for the month, like `2026-02-30`), is shown verbatim rather
 * than guessed at.
 */
export function formatHouseholdDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (match === null) {
    return value
  }
  const monthIndex = Number(match[2]) - 1
  const month = MONTHS[monthIndex]
  if (month === undefined) {
    return value
  }
  const maxDay = monthIndex === 1 && isLeapYear(Number(match[1])) ? 29 : DAYS_IN_MONTH[monthIndex]
  const day = Number(match[3])
  if (day < 1 || day > maxDay) {
    return value
  }
  return `${month} ${day}, ${match[1]}`
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

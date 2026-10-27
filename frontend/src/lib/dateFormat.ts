/**
 * Shared household date/time labels for every product surface (issue #160).
 *
 * Household-local dates reach the client as bare `yyyy-MM-dd` strings. They are
 * labelled straight from that string — never through `new Date(value)`, which
 * parses a bare date as UTC midnight and can shift the day in a negative-offset
 * timezone, and never through `toLocale*`, which is culture-dependent and a
 * silent no-op under this workstation's invariant globalization.
 *
 * Product dates render as `MM-DD-YY` (e.g. `2026-10-05` → `10-05-26`). The API
 * wire format stays ISO `yyyy-MM-dd`; this module is display-only.
 */

/** English month abbreviations, indexed from January (0). */
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Days in each month by January-based index, ignoring leap years. */
const DAYS_IN_MONTH = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]

/** English short weekday labels, indexed from Sunday (0). */
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/**
 * Sakamoto's month offsets, indexed from January (0). The weekday of a calendar
 * date is derived arithmetically from its own year/month/day, so it never
 * depends on `Date`, `Intl`, the device timezone, or the host locale.
 */
const WEEKDAY_MONTH_OFFSETS = [0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4]

/** Gregorian leap-year rule, table-driven so it never depends on ICU/Date. */
function isLeapYear(year: number): boolean {
  return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0
}

/** Pads a non-negative integer to two digits. */
function pad2(value: number): string {
  return String(value).padStart(2, '0')
}

/**
 * Splits a bare household-local `yyyy-MM-dd` into its numeric parts, or returns
 * `null` when the value is not a plain date or carries an impossible month/day
 * (including an impossible day for the month, like `2026-02-30`).
 */
function householdDateParts(value: string): { year: number; month: number; day: number } | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (match === null) {
    return null
  }
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const monthIndex = month - 1
  if (MONTHS[monthIndex] === undefined) {
    return null
  }
  const maxDay = monthIndex === 1 && isLeapYear(year) ? 29 : DAYS_IN_MONTH[monthIndex]
  if (day < 1 || day > maxDay) {
    return null
  }
  return { year, month, day }
}

/**
 * Renders a household-local `yyyy-MM-dd` string as `MM-DD-YY`. A value that is
 * not a bare date, or that carries an impossible month or day (including an
 * impossible day for the month, like `2026-02-30`), is shown verbatim rather
 * than guessed at.
 */
export function formatHouseholdDate(value: string): string {
  const parts = householdDateParts(value)
  if (parts === null) {
    return value
  }
  const yearSuffix = String(parts.year).padStart(4, '0').slice(-2)
  return `${pad2(parts.month)}-${pad2(parts.day)}-${yearSuffix}`
}

/**
 * Renders the English short weekday of a household-local `yyyy-MM-dd` string,
 * e.g. `2026-10-05` → `Mon`. Used where the weekday is the label (the mobile
 * agenda gutter) so it never comes from `toLocaleDateString`, which is
 * culture-dependent in the browser and unavailable under invariant
 * globalization.
 *
 * A value that is not a plain, plausible date yields an empty label: there is no
 * verbatim weekday to fall back to, and a guessed day would be worse than none.
 */
export function formatHouseholdWeekday(value: string): string {
  const parts = householdDateParts(value)
  if (parts === null) {
    return ''
  }
  const { year, month, day } = parts
  const shiftedYear = month < 3 ? year - 1 : year
  const weekdayIndex =
    (shiftedYear +
      Math.floor(shiftedYear / 4) -
      Math.floor(shiftedYear / 100) +
      Math.floor(shiftedYear / 400) +
      WEEKDAY_MONTH_OFFSETS[month - 1] +
      day) %
    7
  return WEEKDAYS[(weekdayIndex + 7) % 7]
}

/**
 * Renders a household-local `yyyy-MM` month key (a paging cursor or heading) as
 * `Mon YYYY`. Month labels stay readable; only full dates use MM-DD-YY.
 * Anything else is shown verbatim.
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

/** Renders a 24-hour clock as `h:mm AM/PM`, e.g. `3:04 PM`. */
export function formatClockTime(hours24: number, minutes: number): string {
  const meridiem = hours24 < 12 ? 'AM' : 'PM'
  const hours = hours24 % 12 === 0 ? 12 : hours24 % 12
  return `${hours}:${pad2(minutes)} ${meridiem}`
}

/** Renders a `Date`'s local calendar day as `MM-DD-YY` (client-clock values). */
export function formatLocalDate(date: Date): string {
  return `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}-${pad2(date.getFullYear() % 100)}`
}

/**
 * Renders an ISO-8601 instant in the browser's local calendar fields as
 * `MM-DD-YY h:mm AM/PM`. A value that does not parse is shown verbatim.
 */
export function formatTimestamp(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }
  return `${formatLocalDate(date)} ${formatClockTime(date.getHours(), date.getMinutes())}`
}

import { monthLabel } from './calendarDates'

/**
 * Pure month-range helpers for the calendar print view (M10-01, docs/M10_PLAN.md
 * §2.1). The print view selects a start month and a month count of 1–12 and
 * prints one month per page in chronological order, including jobs that cross a
 * year boundary. No dates are invented: a month is always a real calendar month.
 */

/** Smallest number of months a print job may span. */
export const PRINT_MONTH_MIN = 1
/** Largest number of months a print job may span. */
export const PRINT_MONTH_MAX = 12

export interface PrintMonth {
  /** Calendar year, e.g. 2026. */
  year: number
  /** Zero-based month index, matching `Date.getMonth()` (0 = January). */
  monthIndex: number
  /** Canonical `YYYY-MM` key, used for the URL param and React keys. */
  key: string
  /** Human heading, e.g. "October 2026". */
  label: string
}

/** Formats a year/month as the canonical `YYYY-MM` print key. */
export function formatMonthKey(year: number, monthIndex: number): string {
  return `${String(year).padStart(4, '0')}-${String(monthIndex + 1).padStart(2, '0')}`
}

/**
 * Parses a `YYYY-MM` key (as carried by `?month=`). Returns `null` for anything
 * that is not a well-formed key, so the caller falls back to the current month.
 */
export function parseMonthKey(
  value: string | null | undefined,
): { year: number; monthIndex: number } | null {
  if (value === null || value === undefined) {
    return null
  }
  const match = /^(\d{4})-(\d{2})$/.exec(value.trim())
  if (match === null) {
    return null
  }
  const year = Number(match[1])
  const month = Number(match[2])
  if (month < 1 || month > 12) {
    return null
  }
  return { year, monthIndex: month - 1 }
}

/**
 * Clamps a month count to the supported 1–12 range. Non-finite and fractional
 * input resolves to a whole number so the resolved list is always printable.
 */
export function clampMonthCount(count: number): number {
  if (!Number.isFinite(count)) {
    return PRINT_MONTH_MIN
  }
  return Math.min(PRINT_MONTH_MAX, Math.max(PRINT_MONTH_MIN, Math.trunc(count)))
}

/**
 * Resolves a start month and count into an ordered list of months, rolling the
 * year forward where the range crosses a boundary. `count` is clamped first, so
 * the result always has between 1 and 12 entries.
 */
export function resolvePrintMonths(
  startYear: number,
  startMonthIndex: number,
  count: number,
): PrintMonth[] {
  const total = clampMonthCount(count)
  const months: PrintMonth[] = []
  for (let offset = 0; offset < total; offset += 1) {
    // `new Date` normalises an out-of-range month index, so December + 1 rolls
    // into January of the next year.
    const date = new Date(startYear, startMonthIndex + offset, 1)
    const year = date.getFullYear()
    const monthIndex = date.getMonth()
    months.push({
      year,
      monthIndex,
      key: formatMonthKey(year, monthIndex),
      label: monthLabel(year, monthIndex),
    })
  }
  return months
}

/** The month containing `today`, used when no `?month=` param is present. */
export function defaultStartMonth(today: Date = new Date()): { year: number; monthIndex: number } {
  return { year: today.getFullYear(), monthIndex: today.getMonth() }
}

/**
 * Resolves the print view's start month: the `?month=` param when it is a valid
 * `YYYY-MM` key, otherwise the month containing `today` (the household's current
 * month, matching the calendar page's initial month).
 */
export function resolveStartMonth(
  monthParam: string | null | undefined,
  today: Date = new Date(),
): { year: number; monthIndex: number } {
  return parseMonthKey(monthParam) ?? defaultStartMonth(today)
}

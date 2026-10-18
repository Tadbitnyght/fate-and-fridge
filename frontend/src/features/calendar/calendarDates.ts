/** Calendar date helpers (M5-09). Dates are household-local ISO `YYYY-MM-DD`. */

import { formatHouseholdDate } from '../../lib/dateFormat'

export function isoDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function parseIsoDate(iso: string): Date {
  const [year, month, day] = iso.split('-').map(Number)
  return new Date(year, month - 1, day)
}

export function addDaysIso(iso: string, days: number): string {
  const date = parseIsoDate(iso)
  date.setDate(date.getDate() + days)
  return isoDate(date)
}

export function monthStartIso(year: number, monthIndex: number): string {
  return isoDate(new Date(year, monthIndex, 1))
}

export function monthEndIso(year: number, monthIndex: number): string {
  return isoDate(new Date(year, monthIndex + 1, 0))
}

export function isSameMonth(iso: string, year: number, monthIndex: number): boolean {
  const date = parseIsoDate(iso)
  return date.getFullYear() === year && date.getMonth() === monthIndex
}

export interface MonthGridDay {
  iso: string
  inMonth: boolean
  dayOfMonth: number
}

/**
 * Builds the weeks shown for a month. The grid starts on the household week
 * start and always contains whole weeks covering the month.
 */
export function buildMonthGrid(
  year: number,
  monthIndex: number,
  weekStartsOn: number,
): MonthGridDay[] {
  const first = new Date(year, monthIndex, 1)
  const offset = (first.getDay() - weekStartsOn + 7) % 7
  const start = new Date(year, monthIndex, 1 - offset)

  const last = new Date(year, monthIndex + 1, 0)
  const trailing = (last.getDay() - weekStartsOn + 7) % 7
  const totalDays = offset + last.getDate() + (6 - trailing)
  const weeks = Math.ceil(totalDays / 7)

  const days: MonthGridDay[] = []
  for (let index = 0; index < weeks * 7; index += 1) {
    const date = new Date(start.getFullYear(), start.getMonth(), start.getDate() + index)
    days.push({
      iso: isoDate(date),
      inMonth: date.getMonth() === monthIndex && date.getFullYear() === year,
      dayOfMonth: date.getDate(),
    })
  }
  return days
}

const MONTH_NAMES = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
]

export function monthLabel(year: number, monthIndex: number): string {
  return `${MONTH_NAMES[monthIndex]} ${year}`
}

/**
 * A full household-local date in the product display format (`MM-DD-YY`), e.g.
 * `10-05-26`. Delegates to the shared formatter so calendar views never keep
 * their own parsing; month labels stay readable via `monthLabel`.
 */
export function fullDateLabel(iso: string): string {
  return formatHouseholdDate(iso)
}

const WEEKDAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** Weekday headers in household order. */
export function weekdayHeaders(weekStartsOn: number): string[] {
  return Array.from({ length: 7 }, (_, index) => WEEKDAY_NAMES[(weekStartsOn + index) % 7])
}

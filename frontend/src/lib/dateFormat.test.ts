import { describe, expect, it } from 'vitest'

import {
  formatClockTime,
  formatHouseholdDate,
  formatHouseholdMonth,
  formatLocalDate,
  formatTimestamp,
} from './dateFormat'

/**
 * Household-local date/month labels shared by every product surface (issue
 * #160). They are labelled straight from the bare string so a timezone can
 * never shift the day; anything that is not a plain, plausible date is shown
 * verbatim instead of guessed at.
 */
describe('formatHouseholdDate', () => {
  it('renders a bare household-local date as `MM-DD-YY`', () => {
    expect(formatHouseholdDate('2026-10-05')).toBe('10-05-26')
    expect(formatHouseholdDate('2026-10-31')).toBe('10-31-26')
    expect(formatHouseholdDate('2025-12-24')).toBe('12-24-25')
  })

  it('pads a single-digit month and day', () => {
    expect(formatHouseholdDate('2026-01-05')).toBe('01-05-26')
    expect(formatHouseholdDate('2026-09-09')).toBe('09-09-26')
  })

  it('renders a year boundary without carrying a Date', () => {
    expect(formatHouseholdDate('2025-12-31')).toBe('12-31-25')
    expect(formatHouseholdDate('2026-01-01')).toBe('01-01-26')
  })

  it('keeps a non-date value verbatim', () => {
    expect(formatHouseholdDate('')).toBe('')
    expect(formatHouseholdDate('2026-11-01T00:00:00Z')).toBe('2026-11-01T00:00:00Z')
    expect(formatHouseholdDate('not-a-date')).toBe('not-a-date')
  })

  it('keeps an impossible month verbatim', () => {
    expect(formatHouseholdDate('2026-00-01')).toBe('2026-00-01')
    expect(formatHouseholdDate('2026-13-01')).toBe('2026-13-01')
    expect(formatHouseholdDate('2026-99-15')).toBe('2026-99-15')
  })

  /* Issue #148: the day was previously rendered unchecked, so `10-00-26`. */
  it('keeps an impossible day verbatim', () => {
    expect(formatHouseholdDate('2026-10-00')).toBe('2026-10-00')
    expect(formatHouseholdDate('2026-02-30')).toBe('2026-02-30')
    expect(formatHouseholdDate('2026-04-31')).toBe('2026-04-31')
    expect(formatHouseholdDate('2026-11-32')).toBe('2026-11-32')
    expect(formatHouseholdDate('2026-11-99')).toBe('2026-11-99')
  })

  it('accepts the first and last valid day of a month', () => {
    expect(formatHouseholdDate('2026-01-01')).toBe('01-01-26')
    expect(formatHouseholdDate('2026-01-31')).toBe('01-31-26')
    expect(formatHouseholdDate('2026-04-30')).toBe('04-30-26')
  })

  it('honours the leap-year rule for February', () => {
    expect(formatHouseholdDate('2024-02-29')).toBe('02-29-24')
    expect(formatHouseholdDate('2000-02-29')).toBe('02-29-00')
    expect(formatHouseholdDate('2025-02-29')).toBe('2025-02-29')
    expect(formatHouseholdDate('1900-02-29')).toBe('1900-02-29')
  })
})

describe('formatHouseholdMonth', () => {
  it('renders a month cursor as `Mon YYYY`', () => {
    expect(formatHouseholdMonth('2026-10')).toBe('Oct 2026')
  })

  it('keeps anything else verbatim', () => {
    expect(formatHouseholdMonth('')).toBe('')
    expect(formatHouseholdMonth('2026-13')).toBe('2026-13')
    expect(formatHouseholdMonth('2026-10-01')).toBe('2026-10-01')
  })
})

describe('formatClockTime', () => {
  it('uses 12 for midnight and noon and pads minutes', () => {
    expect(formatClockTime(0, 5)).toBe('12:05 AM')
    expect(formatClockTime(12, 0)).toBe('12:00 PM')
    expect(formatClockTime(15, 4)).toBe('3:04 PM')
  })
})

describe('formatLocalDate', () => {
  it('renders a Date’s local calendar day as `MM-DD-YY`', () => {
    expect(formatLocalDate(new Date(2026, 0, 5, 23, 59))).toBe('01-05-26')
  })
})

describe('formatTimestamp', () => {
  it('renders an ISO instant in local fields as `MM-DD-YY h:mm AM/PM`', () => {
    // Built from local components, so the round-trip is timezone-independent.
    const local = new Date(2026, 8, 29, 15, 4)
    expect(formatTimestamp(local.toISOString())).toBe('09-29-26 3:04 PM')
  })

  it('keeps an unparseable value verbatim', () => {
    expect(formatTimestamp('not-a-timestamp')).toBe('not-a-timestamp')
  })
})

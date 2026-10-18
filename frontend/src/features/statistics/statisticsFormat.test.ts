import { describe, expect, it } from 'vitest'

import { formatHouseholdDate, formatHouseholdMonth } from './statisticsFormat'

/**
 * Household-local date/month labels (M11-04/M11-05). They are labelled straight
 * from the bare string so a timezone can never shift the day; anything that is
 * not a plain, plausible date is shown verbatim instead of guessed at.
 */
describe('formatHouseholdDate', () => {
  it('renders a bare household-local date as `Mon D, YYYY`', () => {
    expect(formatHouseholdDate('2026-11-01')).toBe('Nov 1, 2026')
    expect(formatHouseholdDate('2025-12-24')).toBe('Dec 24, 2025')
    expect(formatHouseholdDate('2026-10-31')).toBe('Oct 31, 2026')
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

  /* Issue #148: the day was previously rendered unchecked, so "Oct 0, 2026". */
  it('keeps an impossible day verbatim', () => {
    expect(formatHouseholdDate('2026-10-00')).toBe('2026-10-00')
    expect(formatHouseholdDate('2026-02-30')).toBe('2026-02-30')
    expect(formatHouseholdDate('2026-04-31')).toBe('2026-04-31')
    expect(formatHouseholdDate('2026-11-32')).toBe('2026-11-32')
    expect(formatHouseholdDate('2026-11-99')).toBe('2026-11-99')
  })

  it('accepts the first and last valid day of a month', () => {
    expect(formatHouseholdDate('2026-01-01')).toBe('Jan 1, 2026')
    expect(formatHouseholdDate('2026-01-31')).toBe('Jan 31, 2026')
    expect(formatHouseholdDate('2026-04-30')).toBe('Apr 30, 2026')
  })

  it('honours the leap-year rule for February', () => {
    expect(formatHouseholdDate('2024-02-29')).toBe('Feb 29, 2024')
    expect(formatHouseholdDate('2000-02-29')).toBe('Feb 29, 2000')
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

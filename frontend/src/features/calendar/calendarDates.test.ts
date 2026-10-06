import { describe, expect, it } from 'vitest'

import {
  addDaysIso,
  buildMonthGrid,
  isoDate,
  monthEndIso,
  monthStartIso,
  parseIsoDate,
  weekdayHeaders,
} from './calendarDates'

describe('calendar date helpers', () => {
  it('round-trips an ISO date without a UTC shift', () => {
    expect(isoDate(parseIsoDate('2026-11-15'))).toBe('2026-11-15')
  })

  it('adds days across a month boundary', () => {
    expect(addDaysIso('2026-11-30', 1)).toBe('2026-12-01')
    expect(addDaysIso('2026-03-01', -1)).toBe('2026-02-28')
  })

  it('computes month bounds including a leap February', () => {
    expect(monthStartIso(2028, 1)).toBe('2028-02-01')
    expect(monthEndIso(2028, 1)).toBe('2028-02-29')
  })

  it('builds a Sunday-start grid for November 2026', () => {
    // 2026-11-01 is a Sunday.
    const days = buildMonthGrid(2026, 10, 0)
    expect(days[0].iso).toBe('2026-11-01')
    expect(days[0].inMonth).toBe(true)
    expect(days.length % 7).toBe(0)
  })

  it('builds a Monday-start grid with leading days from the previous month', () => {
    const days = buildMonthGrid(2026, 10, 1)
    expect(days[0].iso).toBe('2026-10-26')
    expect(days[0].inMonth).toBe(false)
    expect(days[6].iso).toBe('2026-11-01')
    expect(days[6].inMonth).toBe(true)
  })

  it('orders weekday headers from the household week start', () => {
    expect(weekdayHeaders(0)).toEqual(['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'])
    expect(weekdayHeaders(1)).toEqual(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'])
    expect(weekdayHeaders(6)).toEqual(['Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri'])
  })
})

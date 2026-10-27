import { describe, expect, it } from 'vitest'

import {
  PRINT_MONTH_MAX,
  PRINT_MONTH_MIN,
  clampMonthCount,
  defaultStartMonth,
  formatMonthKey,
  parseMonthKey,
  resolvePrintMonths,
  resolveStartMonth,
} from './printRange'

describe('print range helpers', () => {
  it('formats a canonical YYYY-MM key', () => {
    expect(formatMonthKey(2026, 9)).toBe('2026-10')
    expect(formatMonthKey(2027, 0)).toBe('2027-01')
  })

  it('resolves a single month', () => {
    const months = resolvePrintMonths(2026, 9, 1)
    expect(months).toHaveLength(1)
    expect(months[0]).toMatchObject({
      year: 2026,
      monthIndex: 9,
      key: '2026-10',
      label: 'October 2026',
    })
  })

  it('resolves an ordered range across a year boundary', () => {
    const months = resolvePrintMonths(2026, 10, 3)
    expect(months.map((month) => month.key)).toEqual(['2026-11', '2026-12', '2027-01'])
    expect(months.map((month) => month.label)).toEqual([
      'November 2026',
      'December 2026',
      'January 2027',
    ])
  })

  it('clamps the month count to 1–12', () => {
    expect(clampMonthCount(0)).toBe(PRINT_MONTH_MIN)
    expect(clampMonthCount(-5)).toBe(PRINT_MONTH_MIN)
    expect(clampMonthCount(Number.NaN)).toBe(PRINT_MONTH_MIN)
    expect(clampMonthCount(1)).toBe(1)
    expect(clampMonthCount(12)).toBe(12)
    expect(clampMonthCount(13)).toBe(PRINT_MONTH_MAX)
    expect(clampMonthCount(99)).toBe(PRINT_MONTH_MAX)
    expect(clampMonthCount(3.9)).toBe(3)
  })

  it('always resolves between 1 and 12 months for any count', () => {
    expect(resolvePrintMonths(2026, 0, 0)).toHaveLength(1)
    expect(resolvePrintMonths(2026, 0, 999)).toHaveLength(12)
  })

  it('parses only well-formed YYYY-MM keys', () => {
    expect(parseMonthKey('2026-10')).toEqual({ year: 2026, monthIndex: 9 })
    expect(parseMonthKey(' 2027-01 ')).toEqual({ year: 2027, monthIndex: 0 })
    expect(parseMonthKey('2026-13')).toBeNull()
    expect(parseMonthKey('2026-00')).toBeNull()
    expect(parseMonthKey('2026-1')).toBeNull()
    expect(parseMonthKey('October')).toBeNull()
    expect(parseMonthKey('')).toBeNull()
    expect(parseMonthKey(null)).toBeNull()
    expect(parseMonthKey(undefined)).toBeNull()
  })

  it('prefers the ?month= param and falls back to the current month', () => {
    const today = new Date(2026, 9, 14)
    expect(resolveStartMonth('2026-03', today)).toEqual({ year: 2026, monthIndex: 2 })
    expect(resolveStartMonth(null, today)).toEqual({ year: 2026, monthIndex: 9 })
    expect(resolveStartMonth('not-a-month', today)).toEqual({ year: 2026, monthIndex: 9 })
    expect(defaultStartMonth(today)).toEqual({ year: 2026, monthIndex: 9 })
  })
})

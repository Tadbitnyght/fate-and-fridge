import { describe, expect, it } from 'vitest'

import type { GroceryDepartment } from '../../api/groceries'
import {
  formatGeneratedAt,
  formatQuantityLabel,
  formatRangeSummary,
  formatSourceDate,
  orderDepartments,
} from './groceryFormat'

function department(name: string, isOther: boolean, id: number | null): GroceryDepartment {
  return { department_id: id, name, sort_order: 0, is_other: isOther, items: [] }
}

describe('orderDepartments', () => {
  it('forces the Other group last and preserves server order otherwise', () => {
    const result = orderDepartments([
      department('Other', true, null),
      department('Produce', false, 1),
      department('Bakery', false, 2),
    ])
    expect(result.map((row) => row.name)).toEqual(['Produce', 'Bakery', 'Other'])
  })
})

describe('formatQuantityLabel', () => {
  it('joins a quantity and its unit', () => {
    expect(formatQuantityLabel('1 1/2', 'lb')).toBe('1 1/2 lb')
  })

  it('renders a quantity with no unit on its own', () => {
    expect(formatQuantityLabel('2', null)).toBe('2')
  })

  it('renders a unit-only line when the quantity is empty', () => {
    expect(formatQuantityLabel('', 'lb')).toBe('lb')
  })

  it('renders nothing when the quantity is empty and the unit is null', () => {
    expect(formatQuantityLabel('', null)).toBe('')
  })
})

describe('formatRangeSummary', () => {
  it('renders both ends of a range in MM-DD-YY', () => {
    expect(formatRangeSummary('2026-10-10', '2026-10-16')).toBe('10-10-26 – 10-16-26')
  })

  it('collapses a single-day range to one date', () => {
    expect(formatRangeSummary('2026-10-12', '2026-10-12')).toBe('10-12-26')
  })

  it('keeps unparseable ends verbatim', () => {
    expect(formatRangeSummary('', '2026-10-16')).toBe(' – 10-16-26')
  })
})

describe('formatGeneratedAt', () => {
  it('formats a local clock time as `MM-DD-YY h:mm AM/PM`', () => {
    expect(formatGeneratedAt(new Date(2026, 9, 12, 15, 4))).toBe('10-12-26 3:04 PM')
  })

  it('uses 12 for midnight and noon and pads minutes', () => {
    expect(formatGeneratedAt(new Date(2026, 0, 1, 0, 5))).toBe('01-01-26 12:05 AM')
    expect(formatGeneratedAt(new Date(2026, 11, 31, 12, 0))).toBe('12-31-26 12:00 PM')
  })
})

describe('formatSourceDate', () => {
  it('pairs the shared weekday with the household month/day', () => {
    expect(formatSourceDate('2026-10-06')).toBe('Tue 10/6')
    expect(formatSourceDate('2026-10-05')).toBe('Mon 10/5')
  })

  it('shows an unparseable date verbatim rather than guessing a weekday', () => {
    expect(formatSourceDate('')).toBe('')
    expect(formatSourceDate('2026-02-30')).toBe('2026-02-30')
  })
})

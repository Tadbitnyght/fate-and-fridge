import { describe, expect, it } from 'vitest'

import type { GroceryDepartment } from '../../api/groceries'
import { formatGeneratedAt, formatQuantityLabel, orderDepartments } from './groceryFormat'

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

describe('formatGeneratedAt', () => {
  it('formats a local clock time as `Mon D, YYYY h:mm AM/PM`', () => {
    expect(formatGeneratedAt(new Date(2026, 9, 12, 15, 4))).toBe('Oct 12, 2026 3:04 PM')
  })

  it('uses 12 for midnight and noon and pads minutes', () => {
    expect(formatGeneratedAt(new Date(2026, 0, 1, 0, 5))).toBe('Jan 1, 2026 12:05 AM')
    expect(formatGeneratedAt(new Date(2026, 11, 31, 12, 0))).toBe('Dec 31, 2026 12:00 PM')
  })
})

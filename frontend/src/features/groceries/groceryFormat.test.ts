import { describe, expect, it } from 'vitest'

import type { GroceryDepartment } from '../../api/groceries'
import { orderDepartments } from './groceryFormat'

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

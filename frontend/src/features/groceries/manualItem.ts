import type { GroceryItem } from '../../api/groceries'

/** Editable seed for an existing manual line, derived from the list item. */
export interface ManualItemSeed {
  id: number
  name: string
  quantity: number | null
  unitSymbol: string | null
  ingredientId: number | null
  departmentId: number | null
  saveForFuture: boolean
}

/**
 * The list carries the manual item's identity plus its display values — but not
 * its `unit_id`. The seed keeps the unit symbol so the dialog can map it back
 * through the active units list. Returns `null` for a recipe-derived line.
 */
export function manualSeedFromItem(item: GroceryItem): ManualItemSeed | null {
  if (item.manual_item_id === null) {
    return null
  }
  return {
    id: item.manual_item_id,
    name: item.name,
    quantity: item.quantity,
    unitSymbol: item.unit,
    ingredientId: item.ingredient_id,
    departmentId: item.department_id,
    saveForFuture: false,
  }
}

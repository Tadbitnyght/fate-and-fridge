import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type {
  GroceryManualItemPatch,
  GroceryManualItemRequest,
  GroceryRange,
} from '../../api/groceries'
import type { Department, Ingredient, Unit } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { describeGroceryError } from './groceryFormat'
import type { ManualItemSeed } from './manualItem'
import { useCreateGroceryManualItem, useUpdateGroceryManualItem } from './useGroceries'

function parseQuantity(value: string): number | null {
  const trimmed = value.trim()
  return trimmed.length === 0 ? null : Number(trimmed)
}

/**
 * Add/edit dialog for range-scoped manual grocery items (docs/API.md §13). The
 * name is required; quantity, unit, ingredient, and department are optional.
 * "Save for future" promotes the item to a household staple server-side.
 */
export function ManualItemDialog({
  mode,
  seed,
  range,
  departments,
  units,
  ingredients,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  seed?: ManualItemSeed
  range: GroceryRange
  departments: Department[]
  units: Unit[]
  ingredients: Ingredient[]
  onClose: () => void
  onSaved: (name: string) => void
}) {
  const createItem = useCreateGroceryManualItem()
  const updateItem = useUpdateGroceryManualItem()

  const matchedUnit = seed?.unitSymbol
    ? units.find((unit) => unit.symbol === seed.unitSymbol)
    : undefined
  // The unit could not be mapped (e.g. it was retired): preserve it by leaving
  // `unit_id` out of the update rather than clearing it.
  const preserveUnit = seed?.unitSymbol != null && matchedUnit === undefined

  const [name, setName] = useState(seed?.name ?? '')
  const [quantity, setQuantity] = useState(seed?.quantity != null ? String(seed.quantity) : '')
  const [unitId, setUnitId] = useState(
    matchedUnit ? String(matchedUnit.id) : preserveUnit ? 'keep' : '',
  )
  const [ingredientId, setIngredientId] = useState(
    seed?.ingredientId != null ? String(seed.ingredientId) : '',
  )
  const [departmentId, setDepartmentId] = useState(
    seed?.departmentId != null ? String(seed.departmentId) : '',
  )
  const [saveForFuture, setSaveForFuture] = useState(seed?.saveForFuture ?? false)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)

  const pending = createItem.isPending || updateItem.isPending

  function clearError(field: string): void {
    setErrors((current) => {
      if (!(field in current)) {
        return current
      }
      const next = { ...current }
      delete next[field]
      return next
    })
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setErrors({ name: 'Enter an item name.' })
      return
    }

    let parsedQuantity: number | null
    try {
      parsedQuantity = parseQuantity(quantity)
    } catch {
      parsedQuantity = Number.NaN
    }
    if (parsedQuantity !== null && (!Number.isFinite(parsedQuantity) || parsedQuantity <= 0)) {
      setErrors({ quantity_numeric: 'Enter a number greater than 0, or leave it blank.' })
      return
    }

    const resolvedIngredient = ingredientId === '' ? null : Number(ingredientId)
    const resolvedDepartment = departmentId === '' ? null : Number(departmentId)
    // `keep` means "the stored unit could not be resolved (e.g. retired) —
    // leave `unit_id` untouched"; an explicit number sets it, `''` clears it.
    const keepUnit = unitId === 'keep'
    const resolvedUnit = keepUnit ? undefined : unitId === '' ? null : Number(unitId)

    try {
      if (mode === 'create') {
        const request: GroceryManualItemRequest = {
          name: trimmed,
          quantity_numeric: parsedQuantity,
          ingredient_id: resolvedIngredient,
          department_id: resolvedDepartment,
          unit_id: resolvedUnit ?? null,
          range_start: range.start,
          range_days: range.day_count,
          save_for_future: saveForFuture,
        }
        await createItem.mutateAsync(request)
      } else if (seed) {
        const patch: GroceryManualItemPatch = {
          name: trimmed,
          quantity_numeric: parsedQuantity,
          ingredient_id: resolvedIngredient,
          department_id: resolvedDepartment,
          save_for_future: saveForFuture,
        }
        if (!keepUnit) {
          patch.unit_id = resolvedUnit ?? null
        }
        await updateItem.mutateAsync({ id: seed.id, request: patch })
      }
      onSaved(trimmed)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          return
        }
      }
      setErrors({})
      setFormError(describeGroceryError(error, 'Saving the item failed. Try again.'))
    }
  }

  return (
    <Modal
      title={mode === 'create' ? 'Add grocery item' : `Edit ${seed?.name ?? 'item'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form grocery-form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field id="manual-name" label="Name" error={errors['name']}>
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('manual-name', { error: errors['name'] })}
            value={name}
            onChange={(event) => {
              setName(event.target.value)
              clearError('name')
            }}
          />
        </Field>

        <Field id="manual-quantity" label="Quantity (optional)" error={errors['quantity_numeric']}>
          <input
            type="number"
            min={0}
            step="any"
            inputMode="decimal"
            {...fieldProps('manual-quantity', { error: errors['quantity_numeric'] })}
            value={quantity}
            onChange={(event) => {
              setQuantity(event.target.value)
              clearError('quantity_numeric')
            }}
          />
        </Field>

        <Field id="manual-unit" label="Unit (optional)" error={errors['unit_id']}>
          <select
            {...fieldProps('manual-unit', { error: errors['unit_id'] })}
            value={unitId}
            onChange={(event) => {
              setUnitId(event.target.value)
              clearError('unit_id')
            }}
          >
            <option value="">None</option>
            {preserveUnit && <option value="keep">{seed?.unitSymbol} (keep)</option>}
            {units.map((unit) => (
              <option key={unit.id} value={String(unit.id)}>
                {unit.display_name} ({unit.symbol})
              </option>
            ))}
          </select>
        </Field>

        <Field
          id="manual-ingredient"
          label="Ingredient (optional)"
          hint="Links the line to a canonical ingredient so it can be checked across ranges."
          error={errors['ingredient_id']}
        >
          <select
            {...fieldProps('manual-ingredient', { error: errors['ingredient_id'], hint: true })}
            value={ingredientId}
            onChange={(event) => {
              setIngredientId(event.target.value)
              clearError('ingredient_id')
            }}
          >
            <option value="">None</option>
            {ingredients.map((ingredient) => (
              <option key={ingredient.id} value={String(ingredient.id)}>
                {ingredient.canonical_name}
              </option>
            ))}
          </select>
        </Field>

        <Field
          id="manual-department"
          label="Department (optional)"
          hint="Leave empty to file the item under Other."
          error={errors['department_id']}
        >
          <select
            {...fieldProps('manual-department', { error: errors['department_id'], hint: true })}
            value={departmentId}
            onChange={(event) => {
              setDepartmentId(event.target.value)
              clearError('department_id')
            }}
          >
            <option value="">Other</option>
            {departments.map((department) => (
              <option key={department.id} value={String(department.id)}>
                {department.name}
              </option>
            ))}
          </select>
        </Field>

        <label className="reference-data__checkbox">
          <input
            type="checkbox"
            checked={saveForFuture}
            onChange={(event) => setSaveForFuture(event.target.checked)}
          />
          Save for future
        </label>

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Add item' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

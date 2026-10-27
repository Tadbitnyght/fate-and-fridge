import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { GroceryStaple, GroceryStapleRequest } from '../../api/groceries'
import type { Department, Ingredient, Unit } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { ConfirmDialog, Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { describeGroceryError } from './groceryFormat'
import { useCreateGroceryStaple, useUpdateGroceryStaple } from './useGroceries'

function parseQuantity(value: string): number | null {
  const trimmed = value.trim()
  return trimmed.length === 0 ? null : Number(trimmed)
}

/**
 * Create/edit dialog for household grocery staples (docs/API.md §13). The name
 * is required; ingredient, department, default quantity, and unit are optional.
 * A duplicate active name returns `409 GROCERY_STAPLE_EXISTS`, shown inline on
 * the name field. "Active" is editable only for an existing staple.
 */
export function StapleFormDialog({
  mode,
  staple,
  departments,
  units,
  ingredients,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  staple?: GroceryStaple
  departments: Department[]
  units: Unit[]
  ingredients: Ingredient[]
  onClose: () => void
  onSaved: (staple: GroceryStaple) => void
}) {
  const createStaple = useCreateGroceryStaple()
  const updateStaple = useUpdateGroceryStaple()

  const [name, setName] = useState(staple?.name ?? '')
  const [quantity, setQuantity] = useState(
    staple?.default_quantity_numeric != null ? String(staple.default_quantity_numeric) : '',
  )
  const [unitId, setUnitId] = useState(staple?.unit_id != null ? String(staple.unit_id) : '')
  const [ingredientId, setIngredientId] = useState(
    staple?.ingredient_id != null ? String(staple.ingredient_id) : '',
  )
  const [departmentId, setDepartmentId] = useState(
    staple?.department_id != null ? String(staple.department_id) : '',
  )
  const [isActive, setIsActive] = useState(staple?.is_active ?? true)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [confirmRetire, setConfirmRetire] = useState(false)
  const [retireError, setRetireError] = useState<string | null>(null)

  const pending = createStaple.isPending || updateStaple.isPending

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

  function buildRequest(): GroceryStapleRequest {
    return {
      name: name.trim(),
      ingredient_id: ingredientId === '' ? null : Number(ingredientId),
      department_id: departmentId === '' ? null : Number(departmentId),
      default_quantity_numeric: parseQuantity(quantity),
      unit_id: unitId === '' ? null : Number(unitId),
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setErrors({ name: 'Enter a staple name.' })
      return
    }

    const parsedQuantity = parseQuantity(quantity)
    if (parsedQuantity !== null && (!Number.isFinite(parsedQuantity) || parsedQuantity <= 0)) {
      setErrors({ default_quantity_numeric: 'Enter a number greater than 0, or leave it blank.' })
      return
    }

    // Retiring is a destructive-ish change: confirm before dropping the staple
    // out of the active list (docs/M8_PLAN.md §1.9). Reactivation is harmless.
    if (mode === 'edit' && staple?.is_active && !isActive) {
      setRetireError(null)
      setConfirmRetire(true)
      return
    }

    const request = buildRequest()

    try {
      if (mode === 'create') {
        onSaved(await createStaple.mutateAsync(request))
      } else if (staple) {
        onSaved(
          await updateStaple.mutateAsync({
            id: staple.id,
            request: { ...request, is_active: isActive },
          }),
        )
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.GroceryStapleExists) {
        setErrors({ name: describeGroceryError(error, 'A staple with this name already exists.') })
        return
      }
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          return
        }
      }
      setErrors({})
      setFormError(describeGroceryError(error, 'Saving the staple failed. Try again.'))
    }
  }

  async function handleConfirmRetire(): Promise<void> {
    if (!staple) {
      return
    }
    setRetireError(null)
    try {
      onSaved(
        await updateStaple.mutateAsync({
          id: staple.id,
          request: { ...buildRequest(), is_active: false },
        }),
      )
    } catch (error) {
      setRetireError(describeGroceryError(error, 'Retiring the staple failed. Try again.'))
    }
  }

  if (confirmRetire && staple) {
    return (
      <ConfirmDialog
        title={`Retire ${staple.name}?`}
        message={
          <p>
            It will be removed from the active staples list. You can bring it back later by enabling
            “Include retired”.
          </p>
        }
        confirmLabel="Retire staple"
        pending={updateStaple.isPending}
        error={retireError}
        onConfirm={() => void handleConfirmRetire()}
        onClose={() => {
          setConfirmRetire(false)
          setRetireError(null)
        }}
      />
    )
  }

  return (
    <Modal
      title={mode === 'create' ? 'Add staple' : `Edit ${staple?.name ?? 'staple'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form grocery-form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field id="staple-name" label="Name" error={errors['name']}>
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('staple-name', { error: errors['name'] })}
            value={name}
            onChange={(event) => {
              setName(event.target.value)
              clearError('name')
            }}
          />
        </Field>

        <Field id="staple-ingredient" label="Ingredient (optional)" error={errors['ingredient_id']}>
          <select
            {...fieldProps('staple-ingredient', { error: errors['ingredient_id'] })}
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
          id="staple-department"
          label="Department (optional)"
          hint="Leave empty to file the staple under Other."
          error={errors['department_id']}
        >
          <select
            {...fieldProps('staple-department', { error: errors['department_id'], hint: true })}
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

        <Field
          id="staple-quantity"
          label="Default quantity (optional)"
          error={errors['default_quantity_numeric']}
        >
          <input
            type="number"
            min={0}
            step="any"
            inputMode="decimal"
            {...fieldProps('staple-quantity', { error: errors['default_quantity_numeric'] })}
            value={quantity}
            onChange={(event) => {
              setQuantity(event.target.value)
              clearError('default_quantity_numeric')
            }}
          />
        </Field>

        <Field id="staple-unit" label="Unit (optional)" error={errors['unit_id']}>
          <select
            {...fieldProps('staple-unit', { error: errors['unit_id'] })}
            value={unitId}
            onChange={(event) => {
              setUnitId(event.target.value)
              clearError('unit_id')
            }}
          >
            <option value="">None</option>
            {units.map((unit) => (
              <option key={unit.id} value={String(unit.id)}>
                {unit.display_name} ({unit.symbol})
              </option>
            ))}
          </select>
        </Field>

        {mode === 'edit' && (
          <label className="reference-data__checkbox">
            <input
              type="checkbox"
              checked={isActive}
              onChange={(event) => setIsActive(event.target.checked)}
            />
            Active
          </label>
        )}

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Add staple' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

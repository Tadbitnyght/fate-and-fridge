import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { CreateIngredientRequest, Department, Ingredient } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { useCreateIngredient, useUpdateIngredient } from './useReferenceData'

function clearFieldError(current: FieldErrors, field: string): FieldErrors {
  if (!(field in current)) {
    return current
  }
  const next = { ...current }
  delete next[field]
  return next
}

function ingredientExistsMessage(details: Record<string, unknown>): string {
  const existing = details['existing_id']
  return typeof existing === 'number'
    ? `An ingredient with this name or alias already exists (existing ingredient #${existing}).`
    : 'An ingredient with this name or alias already exists.'
}

function aliasExistsMessage(details: Record<string, unknown>): string {
  const alias = details['alias']
  const subject = typeof alias === 'string' ? `The alias "${alias}"` : 'One of these aliases'
  const existing = details['existing_id']
  return typeof existing === 'number'
    ? `${subject} is already used by another ingredient (existing ingredient #${existing}).`
    : `${subject} is already used by another ingredient.`
}

/** Splits the comma-separated alias input; blank entries are dropped. */
function parseAliasesInput(value: string): string[] {
  return value
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
}

/**
 * Create and edit dialogs for canonical ingredients (docs/API.md §22). The
 * department select offers active departments, but keeps a retired department
 * selectable while it is the ingredient's current value; clearing the select
 * sends an explicit `null` so the server removes the assignment. Aliases are
 * entered at creation only — later changes happen inline in the ingredients
 * table, where `ALIAS_EXISTS` can be surfaced next to the row.
 */
export function IngredientFormDialog({
  mode,
  ingredient,
  departments,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  ingredient?: Ingredient
  departments: Department[]
  onClose: () => void
  onSaved: (result: Ingredient) => void
}) {
  const createIngredient = useCreateIngredient()
  const updateIngredient = useUpdateIngredient()

  const [canonicalName, setCanonicalName] = useState(ingredient?.canonical_name ?? '')
  const [departmentId, setDepartmentId] = useState(
    ingredient?.department_id === null || ingredient?.department_id === undefined
      ? ''
      : String(ingredient.department_id),
  )
  const [aliasesInput, setAliasesInput] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createIngredient.isPending || updateIngredient.isPending

  const departmentOptions = departments.filter(
    (department) => department.is_active || department.id === ingredient?.department_id,
  )

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const trimmedName = canonicalName.trim()
    if (trimmedName.length === 0) {
      setErrors({ canonical_name: 'Enter a canonical name.' })
      return
    }

    try {
      if (mode === 'create') {
        const request: CreateIngredientRequest = { canonical_name: trimmedName }
        if (departmentId !== '') {
          request.department_id = Number(departmentId)
        }
        const aliases = parseAliasesInput(aliasesInput)
        if (aliases.length > 0) {
          request.aliases = aliases
        }
        onSaved(await createIngredient.mutateAsync(request))
      } else if (ingredient) {
        onSaved(
          await updateIngredient.mutateAsync({
            id: ingredient.id,
            request: {
              canonical_name: trimmedName,
              department_id: departmentId === '' ? null : Number(departmentId),
            },
          }),
        )
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.IngredientExists) {
        setErrors({})
        setFormError(ingredientExistsMessage(error.details))
        return
      }
      if (isApiError(error) && error.code === ErrorCodes.AliasExists) {
        setErrors({})
        setFormError(aliasExistsMessage(error.details))
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
      setFormError(
        isApiError(error) ? error.message : 'Saving failed. Check the connection and try again.',
      )
    }
  }

  return (
    <Modal
      title={
        mode === 'create' ? 'Add ingredient' : `Edit ${ingredient?.canonical_name ?? 'ingredient'}`
      }
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="ingredient-name"
          label="Canonical name"
          error={errors['canonical_name']}
          hint="The name used on recipes and grocery lists."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('ingredient-name', { error: errors['canonical_name'], hint: true })}
            value={canonicalName}
            onChange={(event) => {
              setCanonicalName(event.target.value)
              setErrors((current) => clearFieldError(current, 'canonical_name'))
            }}
          />
        </Field>

        <Field
          id="ingredient-department"
          label="Department"
          error={errors['department_id']}
          hint="Grocery section this ingredient is filed under."
        >
          <select
            {...fieldProps('ingredient-department', { error: errors['department_id'], hint: true })}
            value={departmentId}
            onChange={(event) => {
              setDepartmentId(event.target.value)
              setErrors((current) => clearFieldError(current, 'department_id'))
            }}
          >
            <option value="">No department</option>
            {departmentOptions.map((department) => (
              <option key={department.id} value={department.id}>
                {department.is_active ? department.name : `${department.name} (retired)`}
              </option>
            ))}
          </select>
        </Field>

        {mode === 'create' && (
          <Field
            id="ingredient-aliases"
            label="Aliases"
            error={errors['aliases']}
            hint="Optional. Comma-separated alternate names, e.g. tomato, tomatoes."
          >
            <input
              type="text"
              autoComplete="off"
              {...fieldProps('ingredient-aliases', { error: errors['aliases'], hint: true })}
              value={aliasesInput}
              onChange={(event) => {
                setAliasesInput(event.target.value)
                setErrors((current) => clearFieldError(current, 'aliases'))
              }}
            />
          </Field>
        )}

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Add ingredient' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

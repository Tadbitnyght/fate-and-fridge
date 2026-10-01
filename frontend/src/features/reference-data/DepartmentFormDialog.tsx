import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Department } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { useCreateDepartment, useUpdateDepartment } from './useReferenceData'

/**
 * Create and rename dialogs for grocery departments (docs/API.md §22).
 * The server normalizes names and returns `VALIDATION_FAILED` field errors.
 */
export function DepartmentFormDialog({
  mode,
  department,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  department?: Department
  onClose: () => void
  onSaved: (result: Department) => void
}) {
  const createDepartment = useCreateDepartment()
  const updateDepartment = useUpdateDepartment()

  const [name, setName] = useState(department?.name ?? '')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createDepartment.isPending || updateDepartment.isPending

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setErrors({ name: 'Enter a department name.' })
      return
    }

    try {
      if (mode === 'create') {
        onSaved(await createDepartment.mutateAsync(trimmed))
      } else if (department) {
        onSaved(
          await updateDepartment.mutateAsync({ id: department.id, request: { name: trimmed } }),
        )
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          return
        }
      }
      setErrors({})
      setFormError(
        isApiError(error) ? error.message : 'Saving failed. Check the server and try again.',
      )
    }
  }

  return (
    <Modal
      title={mode === 'create' ? 'Add department' : `Rename ${department?.name ?? 'department'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="department-name"
          label="Name"
          error={errors['name']}
          hint="Shown on the grocery list; the server folds case and accents for duplicates."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('department-name', { error: errors['name'], hint: true })}
            value={name}
            onChange={(event) => {
              setName(event.target.value)
              setErrors((current) => {
                if (!('name' in current)) {
                  return current
                }
                const next = { ...current }
                delete next['name']
                return next
              })
            }}
          />
        </Field>

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Create department' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

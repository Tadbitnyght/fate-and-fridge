import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Protein } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { useCreateProtein, useUpdateProtein } from './useReferenceData'

/**
 * Create and rename dialogs for primary proteins (docs/API.md §22). The server
 * normalizes names and answers duplicate names with `PROTEIN_EXISTS`, which is
 * surfaced on the dialog without closing it.
 */
export function ProteinFormDialog({
  mode,
  protein,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  protein?: Protein
  onClose: () => void
  onSaved: (result: Protein) => void
}) {
  const createProtein = useCreateProtein()
  const updateProtein = useUpdateProtein()

  const [name, setName] = useState(protein?.name ?? '')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createProtein.isPending || updateProtein.isPending

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setErrors({ name: 'Enter a protein name.' })
      return
    }

    try {
      if (mode === 'create') {
        onSaved(await createProtein.mutateAsync({ name: trimmed }))
      } else if (protein) {
        onSaved(await updateProtein.mutateAsync({ id: protein.id, request: { name: trimmed } }))
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ProteinExists) {
        const existingId = error.details['existing_id']
        setErrors({})
        setFormError(
          typeof existingId === 'number'
            ? `A protein with this name already exists (existing protein #${existingId}).`
            : 'A protein with this name already exists.',
        )
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
      title={mode === 'create' ? 'Add protein' : `Rename ${protein?.name ?? 'protein'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="protein-name"
          label="Name"
          error={errors['name']}
          hint="Used to space out dinner repeats. Names that differ only by case or accents count as duplicates."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('protein-name', { error: errors['name'], hint: true })}
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
            {pending ? 'Saving…' : mode === 'create' ? 'Add protein' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

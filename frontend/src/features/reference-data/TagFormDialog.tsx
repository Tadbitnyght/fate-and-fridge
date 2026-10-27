import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Tag } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { useCreateTag, useUpdateTag } from './useReferenceData'

/**
 * Create and rename dialogs for recipe tags (docs/API.md §22). Tags have no
 * retirement flag, so the only write paths are create, rename, and Admin
 * delete. Duplicate names come back as `TAG_EXISTS` and stay on the dialog.
 */
export function TagFormDialog({
  mode,
  tag,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  tag?: Tag
  onClose: () => void
  onSaved: (result: Tag) => void
}) {
  const createTag = useCreateTag()
  const updateTag = useUpdateTag()

  const [name, setName] = useState(tag?.name ?? '')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createTag.isPending || updateTag.isPending

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setErrors({ name: 'Enter a tag name.' })
      return
    }

    try {
      if (mode === 'create') {
        onSaved(await createTag.mutateAsync({ name: trimmed }))
      } else if (tag) {
        onSaved(await updateTag.mutateAsync({ id: tag.id, request: { name: trimmed } }))
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.TagExists) {
        const existingId = error.details['existing_id']
        setErrors({})
        setFormError(
          typeof existingId === 'number'
            ? `A tag with this name already exists (existing tag #${existingId}).`
            : 'A tag with this name already exists.',
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
    <Modal title={mode === 'create' ? 'Add tag' : `Rename ${tag?.name ?? 'tag'}`} onClose={onClose}>
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="tag-name"
          label="Name"
          error={errors['name']}
          hint="Used to filter and browse recipes. Names that differ only by case or accents count as duplicates."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('tag-name', { error: errors['name'], hint: true })}
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
            {pending ? 'Saving…' : mode === 'create' ? 'Add tag' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

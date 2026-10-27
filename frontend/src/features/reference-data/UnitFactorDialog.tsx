import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Unit } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors } from '../../utils/fieldErrors'
import { FACTOR_HINT, parseBaseFactorInput } from './unitForm'
import { useUpdateUnit } from './useReferenceData'

/**
 * Base-factor editor for mass/volume units (docs/API.md §22). The factor is
 * validated locally against the documented `1e-6 … 1e6` bounds and any
 * server-side `field_errors.base_factor` is surfaced on the field.
 */
export function UnitFactorDialog({
  unit,
  onClose,
  onSaved,
}: {
  unit: Unit
  onClose: () => void
  onSaved: (result: Unit) => void
}) {
  const updateUnit = useUpdateUnit()
  const [value, setValue] = useState(unit.base_factor === null ? '' : String(unit.base_factor))
  const [error, setError] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const pending = updateUnit.isPending

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const parsed = parseBaseFactorInput(value)
    if (parsed.error) {
      setError(parsed.error)
      return
    }

    try {
      onSaved(
        await updateUnit.mutateAsync({ id: unit.id, request: { base_factor: parsed.factor } }),
      )
    } catch (caught) {
      if (isApiError(caught) && caught.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(caught.details)
        if (serverErrors['base_factor']) {
          setError(serverErrors['base_factor'])
          return
        }
      }
      setError(null)
      setFormError(
        isApiError(caught) ? caught.message : 'Saving failed. Check the connection and try again.',
      )
    }
  }

  return (
    <Modal title={`Edit base factor for ${unit.symbol}`} onClose={onClose}>
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field id="unit-factor" label="Base factor" hint={FACTOR_HINT} error={error ?? undefined}>
          <input
            type="text"
            inputMode="decimal"
            autoComplete="off"
            {...fieldProps('unit-factor', { error: error ?? undefined, hint: true })}
            value={value}
            onChange={(event) => {
              setValue(event.target.value)
              setError(null)
            }}
          />
        </Field>

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : 'Save factor'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

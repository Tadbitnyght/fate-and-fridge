import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { CreateUnitRequest, Unit, UnitDimension } from '../../api/referenceData'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import {
  FACTOR_HINT,
  UNIT_DIMENSIONS,
  UNIT_DIMENSION_LABELS,
  parseBaseFactorInput,
  requiresFactor,
} from './unitForm'
import { useCreateUnit, useUpdateUnit } from './useReferenceData'

function clearFieldError(current: FieldErrors, field: string): FieldErrors {
  if (!(field in current)) {
    return current
  }
  const next = { ...current }
  delete next[field]
  return next
}

function unitExistsMessage(details: Record<string, unknown>): string {
  const existing = details['existing_id']
  return typeof existing === 'number'
    ? `A unit with this symbol or display name already exists (existing unit #${existing}).`
    : 'A unit with this symbol or display name already exists.'
}

/**
 * Create and edit dialogs for measurement units (docs/API.md §22). A unit's
 * dimension is fixed at creation, so the edit form only exposes symbol and
 * display name; the base factor has its own dialog for mass/volume units.
 */
export function UnitFormDialog({
  mode,
  unit,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  unit?: Unit
  onClose: () => void
  onSaved: (result: Unit) => void
}) {
  const createUnit = useCreateUnit()
  const updateUnit = useUpdateUnit()

  const [symbol, setSymbol] = useState(unit?.symbol ?? '')
  const [displayName, setDisplayName] = useState(unit?.display_name ?? '')
  const [dimension, setDimension] = useState<UnitDimension>(unit?.dimension ?? 'mass')
  const [factor, setFactor] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createUnit.isPending || updateUnit.isPending
  const factorRequired = requiresFactor(dimension)

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const trimmedSymbol = symbol.trim()
    const trimmedName = displayName.trim()
    const nextErrors: FieldErrors = {}
    if (trimmedSymbol.length === 0) {
      nextErrors['symbol'] = 'Enter a unit symbol.'
    }
    if (trimmedName.length === 0) {
      nextErrors['display_name'] = 'Enter a display name.'
    }

    let baseFactor: number | undefined
    if (mode === 'create' && factorRequired) {
      const parsed = parseBaseFactorInput(factor)
      if (parsed.error) {
        nextErrors['base_factor'] = parsed.error
      } else {
        baseFactor = parsed.factor
      }
    }

    if (Object.keys(nextErrors).length > 0) {
      setErrors(nextErrors)
      return
    }

    try {
      if (mode === 'create') {
        const request: CreateUnitRequest = {
          symbol: trimmedSymbol,
          display_name: trimmedName,
          dimension,
        }
        if (baseFactor !== undefined) {
          request.base_factor = baseFactor
        }
        onSaved(await createUnit.mutateAsync(request))
      } else if (unit) {
        onSaved(
          await updateUnit.mutateAsync({
            id: unit.id,
            request: { symbol: trimmedSymbol, display_name: trimmedName },
          }),
        )
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.UnitExists) {
        setErrors({})
        setFormError(unitExistsMessage(error.details))
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
      title={mode === 'create' ? 'Add unit' : `Edit ${unit?.symbol ?? 'unit'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="unit-symbol"
          label="Symbol"
          error={errors['symbol']}
          hint="Short form shown on recipes, e.g. g, cup, tsp."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('unit-symbol', { error: errors['symbol'], hint: true })}
            value={symbol}
            onChange={(event) => {
              setSymbol(event.target.value)
              setErrors((current) => clearFieldError(current, 'symbol'))
            }}
          />
        </Field>

        <Field
          id="unit-display-name"
          label="Display name"
          error={errors['display_name']}
          hint="Long form used in lists and grocery output."
        >
          <input
            type="text"
            autoComplete="off"
            {...fieldProps('unit-display-name', { error: errors['display_name'], hint: true })}
            value={displayName}
            onChange={(event) => {
              setDisplayName(event.target.value)
              setErrors((current) => clearFieldError(current, 'display_name'))
            }}
          />
        </Field>

        {mode === 'create' ? (
          <Field
            id="unit-dimension"
            label="Dimension"
            hint="Mass and volume units convert; count and non-convertible units do not."
          >
            <select
              {...fieldProps('unit-dimension', { hint: true })}
              value={dimension}
              onChange={(event) => {
                const next = event.target.value as UnitDimension
                setDimension(next)
                if (!requiresFactor(next)) {
                  setErrors((current) => clearFieldError(current, 'base_factor'))
                }
              }}
            >
              {UNIT_DIMENSIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </Field>
        ) : (
          unit && (
            <p className="form__hint">
              Dimension: {UNIT_DIMENSION_LABELS[unit.dimension]}. A unit&apos;s dimension is fixed
              after creation.
            </p>
          )
        )}

        {mode === 'create' && factorRequired && (
          <Field
            id="unit-base-factor"
            label="Base factor"
            hint={FACTOR_HINT}
            error={errors['base_factor']}
          >
            <input
              type="text"
              inputMode="decimal"
              autoComplete="off"
              {...fieldProps('unit-base-factor', { error: errors['base_factor'], hint: true })}
              value={factor}
              onChange={(event) => {
                setFactor(event.target.value)
                setErrors((current) => clearFieldError(current, 'base_factor'))
              }}
            />
          </Field>
        )}

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Add unit' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

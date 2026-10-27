import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Ingredient, IngredientMergeResult } from '../../api/referenceData'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { useIngredients, useMergeIngredients } from './useReferenceData'

function clearFieldError(current: FieldErrors, field: string): FieldErrors {
  if (!(field in current)) {
    return current
  }
  const next = { ...current }
  delete next[field]
  return next
}

function matchesFilter(ingredient: Ingredient, filter: string): boolean {
  const needle = filter.trim().toLowerCase()
  if (needle.length === 0) {
    return true
  }
  return (
    ingredient.canonical_name.toLowerCase().includes(needle) ||
    ingredient.aliases.some((entry) => entry.alias.toLowerCase().includes(needle))
  )
}

function reassignedText(count: number): string {
  return count === 1 ? '1 reference reassigned.' : `${count} references reassigned.`
}

/**
 * A select of active ingredients with its own filter box. The other side's
 * current selection is excluded so source and target can never be the same row.
 */
function IngredientPicker({
  id,
  label,
  ingredients,
  excludeId,
  value,
  error,
  onChange,
}: {
  id: string
  label: string
  ingredients: Ingredient[]
  excludeId: number | null
  value: number | null
  error?: string
  onChange: (id: number | null) => void
}) {
  const [filter, setFilter] = useState('')
  const options = ingredients.filter(
    (ingredient) => ingredient.id !== excludeId && matchesFilter(ingredient, filter),
  )

  return (
    <div className="form__field">
      <label htmlFor={`${id}-filter`}>Filter {label.toLowerCase()} ingredients</label>
      <input
        id={`${id}-filter`}
        type="search"
        autoComplete="off"
        placeholder="Name or alias…"
        value={filter}
        onChange={(event) => setFilter(event.target.value)}
      />
      <label htmlFor={id}>{label}</label>
      <select
        id={id}
        value={value === null ? '' : String(value)}
        aria-invalid={error ? true : undefined}
        onChange={(event) =>
          onChange(event.target.value === '' ? null : Number(event.target.value))
        }
      >
        <option value="">Select an ingredient…</option>
        {options.map((ingredient) => (
          <option key={ingredient.id} value={ingredient.id}>
            {ingredient.canonical_name}
          </option>
        ))}
      </select>
      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}

interface MergeSummary {
  result: IngredientMergeResult
  sourceName: string
  targetName: string
}

/**
 * Merge dialog (docs/API.md §22): moves every reference and alias from a source
 * ingredient to a target, then retires the source. Names are captured at submit
 * time because the refreshed (active-only) list no longer contains the retired
 * source. `useMergeIngredients` invalidates every ingredient-list variant, so
 * both the table behind the dialog and the pickers refresh.
 */
export function IngredientMergeDialog({ onClose }: { onClose: () => void }) {
  const ingredientsQuery = useIngredients('', false)
  const merge = useMergeIngredients()

  const [sourceId, setSourceId] = useState<number | null>(null)
  const [targetId, setTargetId] = useState<number | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [summary, setSummary] = useState<MergeSummary | null>(null)

  const ingredients = ingredientsQuery.data ?? []

  function nameFor(id: number): string {
    return ingredients.find((entry) => entry.id === id)?.canonical_name ?? `#${id}`
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    setFieldErrors({})

    if (sourceId === null || targetId === null) {
      setFormError('Choose both a source and a target ingredient.')
      return
    }

    const sourceName = nameFor(sourceId)
    const targetName = nameFor(targetId)

    try {
      const result = await merge.mutateAsync({ source_id: sourceId, target_id: targetId })
      setSummary({ result, sourceName, targetName })
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setFieldErrors(serverErrors)
          return
        }
      }
      setFormError(
        isApiError(error) ? error.message : 'Merging failed. Check the connection and try again.',
      )
    }
  }

  return (
    <Modal title="Merge ingredients" onClose={onClose}>
      {summary ? (
        <>
          <div className="modal__body">
            <p>
              Merged {summary.sourceName} into {summary.targetName}.
            </p>
            <p>{reassignedText(summary.result.reassigned_reference_count)}</p>
            {summary.result.skipped_aliases.length > 0 && (
              <p>
                Skipped aliases already present on the target:{' '}
                {summary.result.skipped_aliases.join(', ')}.
              </p>
            )}
            <p className="form__hint">
              The source ingredient is now retired; historical data keeps resolving.
            </p>
          </div>
          <div className="modal__actions">
            <button type="button" className="button--primary" onClick={onClose}>
              Done
            </button>
          </div>
        </>
      ) : (
        <>
          {formError && (
            <p className="form__error" role="alert">
              {formError}
            </p>
          )}

          {ingredientsQuery.isPending ? (
            <p className="page-note">Loading ingredients…</p>
          ) : (
            <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
              <p className="form__hint">
                Every reference and alias moves from the source to the target, then the source is
                retired. This cannot be undone.
              </p>

              <IngredientPicker
                id="merge-source"
                label="Source"
                ingredients={ingredients}
                excludeId={targetId}
                value={sourceId}
                error={fieldErrors['source_id']}
                onChange={(id) => {
                  setSourceId(id)
                  setFieldErrors((current) => clearFieldError(current, 'source_id'))
                }}
              />
              <IngredientPicker
                id="merge-target"
                label="Target"
                ingredients={ingredients}
                excludeId={sourceId}
                value={targetId}
                error={fieldErrors['target_id']}
                onChange={(id) => {
                  setTargetId(id)
                  setFieldErrors((current) => clearFieldError(current, 'target_id'))
                }}
              />

              <div className="modal__actions">
                <button type="button" onClick={onClose} disabled={merge.isPending}>
                  Cancel
                </button>
                <button
                  type="submit"
                  className="button--primary"
                  disabled={merge.isPending || sourceId === null || targetId === null}
                >
                  {merge.isPending ? 'Merging…' : 'Merge'}
                </button>
              </div>
            </form>
          )}
        </>
      )}
    </Modal>
  )
}

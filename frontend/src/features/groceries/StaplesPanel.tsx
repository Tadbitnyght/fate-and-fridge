import { useState } from 'react'

import type { GroceryRange, GroceryStaple } from '../../api/groceries'
import type { Department, Ingredient, Unit } from '../../api/referenceData'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { describeGroceryError } from './groceryFormat'
import { StapleFormDialog } from './StapleFormDialog'
import { useAddGroceryStapleToList, useGroceryStaples } from './useGroceries'

type DialogState = { kind: 'create' } | { kind: 'edit'; staple: GroceryStaple }

function formatQuantity(staple: GroceryStaple, units: Unit[]): string {
  if (staple.default_quantity_numeric === null) {
    return ''
  }
  const unit = staple.unit_id !== null ? units.find((row) => row.id === staple.unit_id) : undefined
  return unit
    ? `${staple.default_quantity_numeric} ${unit.symbol}`
    : String(staple.default_quantity_numeric)
}

/**
 * Household staple catalog management.
 * Staples are never auto-added; "Add to list" creates a manual item for the
 * active range. Reads need `groceries.view`; every write needs `groceries.edit`
 * but `canEdit` here is UI visibility only — the server is the sole gate.
 */
export function StaplesPanel({
  canEdit,
  range,
  departments,
  units,
  ingredients,
  onAnnounce,
}: {
  canEdit: boolean
  range: GroceryRange | null
  departments: Department[]
  units: Unit[]
  ingredients: Ingredient[]
  onAnnounce: (message: string) => void
}) {
  const [includeRetired, setIncludeRetired] = useState(false)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [pendingStapleId, setPendingStapleId] = useState<number | null>(null)

  const staplesQuery = useGroceryStaples(includeRetired)
  const addToList = useAddGroceryStapleToList()

  async function handleAddToList(staple: GroceryStaple): Promise<void> {
    if (!range) {
      return
    }
    setActionError(null)
    setPendingStapleId(staple.id)
    try {
      await addToList.mutateAsync({
        id: staple.id,
        rangeStart: range.start,
        rangeDays: range.day_count,
      })
      onAnnounce(`Added ${staple.name} to the grocery list.`)
    } catch (error) {
      setActionError(
        describeGroceryError(error, 'Adding the staple to the list failed. Try again.'),
      )
    } finally {
      setPendingStapleId(null)
    }
  }

  const staples = staplesQuery.data?.staples ?? []

  return (
    <section className="grocery-staples" aria-labelledby="grocery-staples-heading">
      <div className="grocery-staples__header">
        <h3 id="grocery-staples-heading">Staples</h3>
        {canEdit && (
          <button
            type="button"
            className="button--primary"
            onClick={() => {
              setActionError(null)
              setDialog({ kind: 'create' })
            }}
          >
            Add staple
          </button>
        )}
      </div>

      <p className="page-note">
        Reusable items you add to a list on demand. Adding one creates a manual item for the current
        range.
      </p>

      <label className="reference-data__checkbox">
        <input
          type="checkbox"
          checked={includeRetired}
          onChange={(event) => setIncludeRetired(event.target.checked)}
        />
        Include retired
      </label>

      {actionError && (
        <p className="form__error" role="alert">
          {actionError}
        </p>
      )}

      {staplesQuery.isPending && <LoadingScreen label="Loading staples…" />}

      {staplesQuery.isError && (
        <ErrorScreen
          title="Cannot load staples"
          error={staplesQuery.error}
          onRetry={() => void staplesQuery.refetch()}
        />
      )}

      {staplesQuery.data &&
        (staples.length === 0 ? (
          <p className="page-note">
            {includeRetired
              ? 'No staples yet. Add one to reuse it later.'
              : 'No active staples. Add one, or include retired staples.'}
          </p>
        ) : (
          <ul className="grocery-staples__list" aria-label="Staples">
            {staples.map((staple) => {
              const quantity = formatQuantity(staple, units)
              const ingredient =
                staple.ingredient_id !== null
                  ? ingredients.find((row) => row.id === staple.ingredient_id)?.canonical_name
                  : undefined
              const department =
                staple.department_id !== null
                  ? departments.find((row) => row.id === staple.department_id)?.name
                  : undefined
              const pending = pendingStapleId === staple.id

              return (
                <li key={staple.id} className="grocery-staple">
                  <div className="grocery-staple__body">
                    <span className="grocery-staple__name">{staple.name}</span>
                    {!staple.is_active && (
                      <span className="grocery-staple__badge">
                        <span aria-hidden="true">⏸</span> Retired
                      </span>
                    )}
                    {quantity.length > 0 && (
                      <span className="grocery-staple__quantity">{quantity}</span>
                    )}
                    {(ingredient || department) && (
                      <span className="grocery-staple__meta">
                        {[ingredient, department].filter(Boolean).join(' · ')}
                      </span>
                    )}
                  </div>
                  <div className="grocery-staple__actions">
                    <button
                      type="button"
                      disabled={!canEdit || !staple.is_active || range === null || pending}
                      onClick={() => void handleAddToList(staple)}
                    >
                      {pending ? 'Adding…' : 'Add to list'}
                    </button>
                    {canEdit && (
                      <button
                        type="button"
                        aria-label={`Edit ${staple.name}`}
                        onClick={() => {
                          setActionError(null)
                          setDialog({ kind: 'edit', staple })
                        }}
                      >
                        Edit
                      </button>
                    )}
                  </div>
                </li>
              )
            })}
          </ul>
        ))}

      {dialog?.kind === 'create' && (
        <StapleFormDialog
          mode="create"
          departments={departments}
          units={units}
          ingredients={ingredients}
          onClose={() => setDialog(null)}
          onSaved={(saved) => {
            setDialog(null)
            onAnnounce(`Added staple ${saved.name}.`)
          }}
        />
      )}

      {dialog?.kind === 'edit' && (
        <StapleFormDialog
          mode="edit"
          staple={dialog.staple}
          departments={departments}
          units={units}
          ingredients={ingredients}
          onClose={() => setDialog(null)}
          onSaved={(saved) => {
            setDialog(null)
            onAnnounce(
              saved.is_active
                ? `Updated staple ${saved.name}.`
                : `Retired staple ${saved.name}. It no longer appears in the active list.`,
            )
          }}
        />
      )}
    </section>
  )
}

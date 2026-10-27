import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Unit } from '../../api/referenceData'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useReferenceDataAccess } from './access'
import { UnitFactorDialog } from './UnitFactorDialog'
import { UnitFormDialog } from './UnitFormDialog'
import { UNIT_DIMENSION_LABELS, requiresFactor } from './unitForm'
import {
  useAddUnitAlias,
  useDeleteUnit,
  useRemoveUnitAlias,
  useRetireUnit,
  useUnits,
} from './useReferenceData'

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; unit: Unit }
  | { kind: 'factor'; unit: Unit }
  | { kind: 'delete'; unit: Unit }

function inUseMessage(count: number): string {
  return count === 1
    ? '1 recipe ingredient uses this unit. Retire it instead, or update that recipe first.'
    : `${count} recipe ingredients use this unit. Retire it instead, or update those recipes first.`
}

function describeError(error: unknown, fallback: string): string {
  return isApiError(error) ? error.message : fallback
}

function factorText(unit: Unit): string {
  if (!requiresFactor(unit.dimension) || unit.base_factor === null) {
    return '—'
  }
  return String(unit.base_factor)
}

/** Inline alias add/remove for one unit row (docs/API.md §22). */
function UnitAliasEditor({ unit, canEdit }: { unit: Unit; canEdit: boolean }) {
  const addAlias = useAddUnitAlias()
  const removeAlias = useRemoveUnitAlias()
  const [alias, setAlias] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [removingId, setRemovingId] = useState<number | null>(null)

  async function handleAdd(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    const trimmed = alias.trim()
    if (trimmed.length === 0) {
      return
    }
    setError(null)
    try {
      await addAlias.mutateAsync({ id: unit.id, alias: trimmed })
      setAlias('')
    } catch (caught) {
      setError(
        describeError(caught, 'Adding the alias failed. Check the connection and try again.'),
      )
    }
  }

  async function handleRemove(aliasId: number): Promise<void> {
    setError(null)
    setRemovingId(aliasId)
    try {
      await removeAlias.mutateAsync({ id: unit.id, aliasId })
    } catch (caught) {
      setError(
        describeError(caught, 'Removing the alias failed. Check the connection and try again.'),
      )
    } finally {
      setRemovingId(null)
    }
  }

  return (
    <div className="reference-data__aliases">
      {unit.aliases.length === 0 ? (
        <span className="reference-data__alias-empty">—</span>
      ) : (
        <ul className="reference-data__alias-list">
          {unit.aliases.map((entry) => (
            <li key={entry.id}>
              <span>{entry.alias}</span>
              {canEdit && (
                <button
                  type="button"
                  aria-label={`Remove alias ${entry.alias} from ${unit.symbol}`}
                  disabled={removeAlias.isPending && removingId === entry.id}
                  onClick={() => void handleRemove(entry.id)}
                >
                  Remove
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {canEdit && (
        <form className="reference-data__alias-form" onSubmit={(event) => void handleAdd(event)}>
          <label className="sr-only" htmlFor={`unit-alias-${unit.id}`}>
            New alias for {unit.symbol}
          </label>
          <input
            id={`unit-alias-${unit.id}`}
            type="text"
            autoComplete="off"
            value={alias}
            onChange={(event) => {
              setAlias(event.target.value)
              setError(null)
            }}
          />
          <button
            type="submit"
            aria-label={`Add alias to ${unit.symbol}`}
            disabled={addAlias.isPending}
          >
            Add
          </button>
        </form>
      )}

      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}

/** Units tab (docs/API.md §22): CRUD, dimension-fixed edits, factor editor, aliases. */
export function UnitsTab() {
  const { canEdit, isAdmin } = useReferenceDataAccess()

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [includeRetired, setIncludeRetired] = useState(false)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const unitsQuery = useUnits(search, includeRetired)
  const retireUnit = useRetireUnit()
  const deleteUnit = useDeleteUnit()

  const rows = unitsQuery.data ?? []
  const visible = includeRetired ? rows : rows.filter((unit) => unit.is_active)
  const showActions = canEdit || isAdmin

  function handleSearch(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    setSearch(searchInput.trim())
    setActionError(null)
  }

  function clearSearch(): void {
    setSearchInput('')
    setSearch('')
  }

  async function handleRetire(unit: Unit): Promise<void> {
    setActionError(null)
    try {
      await retireUnit.mutateAsync(unit.id)
    } catch (error) {
      setActionError(describeError(error, 'The action failed. Check the connection and try again.'))
    }
  }

  async function handleDelete(): Promise<void> {
    if (dialog?.kind !== 'delete') {
      return
    }
    setDeleteError(null)
    try {
      await deleteUnit.mutateAsync(dialog.unit.id)
      setDialog(null)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.UnitInUse) {
        const count = error.details['usage_count']
        setDeleteError(
          typeof count === 'number'
            ? inUseMessage(count)
            : 'This unit is still in use. Retire it instead.',
        )
        return
      }
      setDeleteError(describeError(error, 'Delete failed. Check the connection and try again.'))
    }
  }

  if (unitsQuery.isPending) {
    return <LoadingScreen label="Loading units…" />
  }

  if (unitsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load units"
        error={unitsQuery.error}
        onRetry={() => void unitsQuery.refetch()}
      />
    )
  }

  return (
    <section className="settings-subsection reference-data__tab" aria-labelledby="units-heading">
      <div className="settings-section__header">
        <h4 id="units-heading">Units</h4>
        {canEdit && (
          <button
            type="button"
            className="button--primary"
            onClick={() => {
              setActionError(null)
              setDialog({ kind: 'create' })
            }}
          >
            Add unit
          </button>
        )}
      </div>

      {actionError && (
        <p className="form__error" role="alert">
          {actionError}
        </p>
      )}

      <form className="reference-data__toolbar" onSubmit={handleSearch}>
        <div className="form__field reference-data__search">
          <label htmlFor="unit-search">Search units</label>
          <input
            id="unit-search"
            type="search"
            value={searchInput}
            placeholder="Symbol, name, or alias…"
            onChange={(event) => setSearchInput(event.target.value)}
          />
        </div>
        <button type="submit">Search</button>
        {search.length > 0 && (
          <button type="button" onClick={clearSearch}>
            Clear
          </button>
        )}
        <label className="reference-data__checkbox">
          <input
            type="checkbox"
            checked={includeRetired}
            onChange={(event) => setIncludeRetired(event.target.checked)}
          />
          Include retired
        </label>
      </form>

      {visible.length === 0 ? (
        <p className="page-note">
          {search.length > 0
            ? 'No units match this search.'
            : 'No units yet. Add the first one to start recording measurements.'}
        </p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Units of measure</caption>
            <thead>
              <tr>
                <th scope="col">Symbol</th>
                <th scope="col">Display name</th>
                <th scope="col">Dimension</th>
                <th scope="col">Base factor</th>
                <th scope="col">Aliases</th>
                <th scope="col">Status</th>
                {showActions && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((unit) => (
                <tr key={unit.id}>
                  <th scope="row">{unit.symbol}</th>
                  <td>{unit.display_name}</td>
                  <td>{UNIT_DIMENSION_LABELS[unit.dimension]}</td>
                  <td>{factorText(unit)}</td>
                  <td>
                    <UnitAliasEditor unit={unit} canEdit={canEdit} />
                  </td>
                  <td>{unit.is_active ? 'Active' : 'Retired'}</td>
                  {showActions && (
                    <td className="data-table__actions reference-data__actions">
                      {canEdit && (
                        <>
                          <button
                            type="button"
                            aria-label={`Edit ${unit.symbol}`}
                            onClick={() => {
                              setActionError(null)
                              setDialog({ kind: 'edit', unit })
                            }}
                          >
                            Edit
                          </button>
                          {requiresFactor(unit.dimension) && (
                            <button
                              type="button"
                              aria-label={`Edit factor for ${unit.symbol}`}
                              onClick={() => {
                                setActionError(null)
                                setDialog({ kind: 'factor', unit })
                              }}
                            >
                              Edit factor
                            </button>
                          )}
                          {unit.is_active && (
                            <button
                              type="button"
                              aria-label={`Retire ${unit.symbol}`}
                              disabled={retireUnit.isPending}
                              onClick={() => void handleRetire(unit)}
                            >
                              Retire
                            </button>
                          )}
                        </>
                      )}
                      {isAdmin && (
                        <button
                          type="button"
                          aria-label={`Delete ${unit.symbol}`}
                          onClick={() => {
                            setDeleteError(null)
                            setDialog({ kind: 'delete', unit })
                          }}
                        >
                          Delete
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {dialog?.kind === 'create' && (
        <UnitFormDialog
          mode="create"
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'edit' && (
        <UnitFormDialog
          mode="edit"
          unit={dialog.unit}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'factor' && (
        <UnitFactorDialog
          unit={dialog.unit}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.unit.symbol}?`}
          message={
            <p>
              This permanently removes the unit. Units still referenced by recipe ingredients cannot
              be deleted — retire them instead so existing data keeps resolving.
            </p>
          }
          confirmLabel="Delete unit"
          pending={deleteUnit.isPending}
          error={deleteError}
          onConfirm={() => void handleDelete()}
          onClose={() => {
            setDialog(null)
            setDeleteError(null)
          }}
        />
      )}
    </section>
  )
}

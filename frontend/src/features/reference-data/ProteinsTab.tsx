import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Protein } from '../../api/referenceData'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useReferenceDataAccess } from './access'
import { ProteinFormDialog } from './ProteinFormDialog'
import {
  useDeleteProtein,
  useProteins,
  useRetireProtein,
  useUpdateProtein,
} from './useReferenceData'

type DialogState =
  { kind: 'create' } | { kind: 'edit'; protein: Protein } | { kind: 'delete'; protein: Protein }

function inUseMessage(count: number): string {
  return count === 1
    ? '1 recipe uses this protein. Retire it instead, or update that recipe first.'
    : `${count} recipes use this protein. Retire it instead, or update those recipes first.`
}

function describeError(error: unknown, fallback: string): string {
  return isApiError(error) ? error.message : fallback
}

/**
 * Proteins tab (docs/API.md §22): primary proteins used to space out dinner
 * repeats. Writes need `reference_data.edit`, hard delete is Admin-only, and
 * retirement is reversible through `PATCH { is_active: true }`.
 */
export function ProteinsTab() {
  const { canEdit, isAdmin } = useReferenceDataAccess()

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [includeRetired, setIncludeRetired] = useState(false)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const proteinsQuery = useProteins(search, includeRetired)
  const updateProtein = useUpdateProtein()
  const retireProtein = useRetireProtein()
  const deleteProtein = useDeleteProtein()

  const rows = proteinsQuery.data ?? []
  const visible = includeRetired ? rows : rows.filter((protein) => protein.is_active)
  const actionPending = updateProtein.isPending || retireProtein.isPending
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

  async function handleToggleActive(protein: Protein): Promise<void> {
    setActionError(null)
    try {
      if (protein.is_active) {
        await retireProtein.mutateAsync(protein.id)
      } else {
        await updateProtein.mutateAsync({ id: protein.id, request: { is_active: true } })
      }
    } catch (error) {
      setActionError(describeError(error, 'The action failed. Check the server and try again.'))
    }
  }

  async function handleDelete(): Promise<void> {
    if (dialog?.kind !== 'delete') {
      return
    }
    setDeleteError(null)
    try {
      await deleteProtein.mutateAsync(dialog.protein.id)
      setDialog(null)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ProteinInUse) {
        const count = error.details['usage_count']
        setDeleteError(
          typeof count === 'number'
            ? inUseMessage(count)
            : 'This protein is still in use. Retire it instead.',
        )
        return
      }
      setDeleteError(describeError(error, 'Delete failed. Check the server and try again.'))
    }
  }

  if (proteinsQuery.isPending) {
    return <LoadingScreen label="Loading proteins…" />
  }

  if (proteinsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load proteins"
        error={proteinsQuery.error}
        onRetry={() => void proteinsQuery.refetch()}
      />
    )
  }

  return (
    <section className="settings-subsection reference-data__tab" aria-labelledby="proteins-heading">
      <div className="settings-section__header">
        <h4 id="proteins-heading">Proteins</h4>
        {canEdit && (
          <button
            type="button"
            className="button--primary"
            onClick={() => {
              setActionError(null)
              setDialog({ kind: 'create' })
            }}
          >
            Add protein
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
          <label htmlFor="protein-search">Search proteins</label>
          <input
            id="protein-search"
            type="search"
            value={searchInput}
            placeholder="Name contains…"
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
            ? 'No proteins match this search.'
            : 'No proteins yet. Add the first one to start spacing out dinner repeats.'}
        </p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Proteins</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                {showActions && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((protein) => (
                <tr key={protein.id}>
                  <th scope="row">{protein.name}</th>
                  <td>{protein.is_active ? 'Active' : 'Retired'}</td>
                  {showActions && (
                    <td className="data-table__actions reference-data__actions">
                      {canEdit && (
                        <>
                          <button
                            type="button"
                            aria-label={`Rename ${protein.name}`}
                            onClick={() => {
                              setActionError(null)
                              setDialog({ kind: 'edit', protein })
                            }}
                          >
                            Rename
                          </button>
                          {protein.is_active ? (
                            <button
                              type="button"
                              aria-label={`Retire ${protein.name}`}
                              disabled={actionPending}
                              onClick={() => void handleToggleActive(protein)}
                            >
                              Retire
                            </button>
                          ) : (
                            <button
                              type="button"
                              aria-label={`Enable ${protein.name}`}
                              disabled={actionPending}
                              onClick={() => void handleToggleActive(protein)}
                            >
                              Enable
                            </button>
                          )}
                        </>
                      )}
                      {isAdmin && (
                        <button
                          type="button"
                          aria-label={`Delete ${protein.name}`}
                          onClick={() => {
                            setDeleteError(null)
                            setDialog({ kind: 'delete', protein })
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
        <ProteinFormDialog
          mode="create"
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'edit' && (
        <ProteinFormDialog
          mode="edit"
          protein={dialog.protein}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.protein.name}?`}
          message={
            <p>
              This permanently removes the protein. Proteins still referenced by recipes cannot be
              deleted — retire them instead so existing data keeps resolving.
            </p>
          }
          confirmLabel="Delete protein"
          pending={deleteProtein.isPending}
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

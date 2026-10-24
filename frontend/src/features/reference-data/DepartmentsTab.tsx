import { useMemo, useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Department } from '../../api/referenceData'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useReferenceDataAccess } from './access'
import { DepartmentFormDialog } from './DepartmentFormDialog'
import {
  useDeleteDepartment,
  useDepartments,
  useReorderDepartments,
  useRetireDepartment,
  useUpdateDepartment,
} from './useReferenceData'

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; department: Department }
  | { kind: 'delete'; department: Department }

function inUseMessage(count: number): string {
  return count === 1
    ? '1 ingredient uses this department. Retire it instead, or move that ingredient first.'
    : `${count} ingredients use this department. Retire it instead, or move those ingredients first.`
}

function describeError(error: unknown, fallback: string): string {
  return isApiError(error) ? error.message : fallback
}

/** Departments tab (docs/API.md §22): ordered list, CRUD, retire, reorder. */
export function DepartmentsTab() {
  const { canEdit, isAdmin } = useReferenceDataAccess()

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [includeRetired, setIncludeRetired] = useState(false)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const departmentsQuery = useDepartments(search)
  const updateDepartment = useUpdateDepartment()
  const retireDepartment = useRetireDepartment()
  const deleteDepartment = useDeleteDepartment()
  const reorderDepartments = useReorderDepartments()

  // The query always includes retired rows, so this is the full ordered list
  // and its ids are a valid `PUT /departments/order` permutation.
  const ordered = useMemo(() => {
    const rows = departmentsQuery.data ?? []
    return [...rows].sort((left, right) => left.sort_order - right.sort_order)
  }, [departmentsQuery.data])

  const visible = includeRetired ? ordered : ordered.filter((row) => row.is_active)
  const actionPending = updateDepartment.isPending || retireDepartment.isPending
  const reorderPending = reorderDepartments.isPending
  // A search response is only a subset, so it cannot form a full permutation.
  const reorderDisabled = reorderPending || search.length > 0
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

  function handleMove(department: Department, direction: -1 | 1): void {
    const visibleIndex = visible.findIndex((row) => row.id === department.id)
    const neighbour = visible[visibleIndex + direction]
    if (!neighbour) {
      return
    }

    // Swap the two visible rows in the full ordered list; hidden retired rows
    // keep their slots but still travel in the permutation.
    const next = [...ordered]
    const from = next.findIndex((row) => row.id === department.id)
    const to = next.findIndex((row) => row.id === neighbour.id)
    ;[next[from], next[to]] = [next[to], next[from]]

    setActionError(null)
    void reorderDepartments
      .mutateAsync(next.map((row) => row.id))
      .catch((error: unknown) =>
        setActionError(describeError(error, 'Reordering failed. Check the server and try again.')),
      )
  }

  async function handleToggleActive(department: Department): Promise<void> {
    setActionError(null)
    try {
      if (department.is_active) {
        await retireDepartment.mutateAsync(department.id)
      } else {
        await updateDepartment.mutateAsync({ id: department.id, request: { is_active: true } })
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
      await deleteDepartment.mutateAsync(dialog.department.id)
      setDialog(null)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.DepartmentInUse) {
        const count = error.details['ingredient_count']
        setDeleteError(
          typeof count === 'number'
            ? inUseMessage(count)
            : 'This department is still in use. Retire it instead.',
        )
        return
      }
      setDeleteError(describeError(error, 'Delete failed. Check the server and try again.'))
    }
  }

  if (departmentsQuery.isPending) {
    return <LoadingScreen label="Loading departments…" />
  }

  if (departmentsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load departments"
        error={departmentsQuery.error}
        onRetry={() => void departmentsQuery.refetch()}
      />
    )
  }

  return (
    <section
      className="settings-subsection reference-data__tab"
      aria-labelledby="departments-heading"
    >
      <div className="settings-section__header">
        <h4 id="departments-heading">Departments</h4>
        {canEdit && (
          <button
            type="button"
            className="button--primary"
            onClick={() => {
              setActionError(null)
              setDialog({ kind: 'create' })
            }}
          >
            Add department
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
          <label htmlFor="department-search">Search departments</label>
          <input
            id="department-search"
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

      {search.length > 0 && <p className="form__hint">Clear the search to reorder departments.</p>}

      {visible.length === 0 ? (
        <p className="page-note">
          {search.length > 0
            ? 'No departments match this search.'
            : 'No departments yet. Add the first one to start grouping groceries.'}
        </p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Grocery departments</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                {showActions && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((department, index) => (
                <tr key={department.id}>
                  <th scope="row">{department.name}</th>
                  <td>{department.is_active ? 'Active' : 'Retired'}</td>
                  {showActions && (
                    <td className="data-table__actions reference-data__actions">
                      {canEdit && (
                        <>
                          <button
                            type="button"
                            aria-label={`Move up ${department.name}`}
                            disabled={reorderDisabled || index === 0}
                            onClick={() => handleMove(department, -1)}
                          >
                            Move up
                          </button>
                          <button
                            type="button"
                            aria-label={`Move down ${department.name}`}
                            disabled={reorderDisabled || index === visible.length - 1}
                            onClick={() => handleMove(department, 1)}
                          >
                            Move down
                          </button>
                          <button
                            type="button"
                            aria-label={`Rename ${department.name}`}
                            onClick={() => {
                              setActionError(null)
                              setDialog({ kind: 'edit', department })
                            }}
                          >
                            Rename
                          </button>
                          {department.is_active ? (
                            <button
                              type="button"
                              aria-label={`Retire ${department.name}`}
                              disabled={actionPending}
                              onClick={() => void handleToggleActive(department)}
                            >
                              Retire
                            </button>
                          ) : (
                            <button
                              type="button"
                              aria-label={`Enable ${department.name}`}
                              disabled={actionPending}
                              onClick={() => void handleToggleActive(department)}
                            >
                              Enable
                            </button>
                          )}
                        </>
                      )}
                      {isAdmin && (
                        <button
                          type="button"
                          aria-label={`Delete ${department.name}`}
                          onClick={() => {
                            setDeleteError(null)
                            setDialog({ kind: 'delete', department })
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
        <DepartmentFormDialog
          mode="create"
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'edit' && (
        <DepartmentFormDialog
          mode="edit"
          department={dialog.department}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.department.name}?`}
          message={
            <p>
              This permanently removes the department. Departments still referenced by ingredients
              cannot be deleted — retire them instead so existing data keeps resolving.
            </p>
          }
          confirmLabel="Delete department"
          pending={deleteDepartment.isPending}
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

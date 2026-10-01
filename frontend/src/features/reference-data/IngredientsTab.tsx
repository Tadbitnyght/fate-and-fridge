import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Ingredient } from '../../api/referenceData'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useReferenceDataAccess } from './access'
import { IngredientFormDialog } from './IngredientFormDialog'
import { IngredientMergeDialog } from './IngredientMergeDialog'
import {
  useAddIngredientAlias,
  useDeleteIngredient,
  useDepartments,
  useIngredients,
  useRemoveIngredientAlias,
  useRetireIngredient,
} from './useReferenceData'

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; ingredient: Ingredient }
  | { kind: 'merge' }
  | { kind: 'delete'; ingredient: Ingredient }

function describeError(error: unknown, fallback: string): string {
  return isApiError(error) ? error.message : fallback
}

function inUseMessage(details: Record<string, unknown>): string {
  if (details['reason'] === 'merge_reference') {
    return 'This ingredient is kept as the source or target of an ingredient merge, so it cannot be deleted. It stays so historical data keeps resolving.'
  }
  const count = details['usage_count']
  if (typeof count === 'number') {
    return count === 1
      ? '1 recipe ingredient references this ingredient. Retire it instead, or update that recipe first.'
      : `${count} recipe ingredients reference this ingredient. Retire it instead, or update those recipes first.`
  }
  return 'This ingredient is still in use. Retire it instead.'
}

/** Inline alias add/remove for one ingredient row (docs/API.md §22). */
function IngredientAliasEditor({
  ingredient,
  canEdit,
}: {
  ingredient: Ingredient
  canEdit: boolean
}) {
  const addAlias = useAddIngredientAlias()
  const removeAlias = useRemoveIngredientAlias()
  const [alias, setAlias] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [removingId, setRemovingId] = useState<number | null>(null)
  const name = ingredient.canonical_name

  async function handleAdd(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    const trimmed = alias.trim()
    if (trimmed.length === 0) {
      return
    }
    setError(null)
    try {
      await addAlias.mutateAsync({ id: ingredient.id, alias: trimmed })
      setAlias('')
    } catch (caught) {
      setError(describeError(caught, 'Adding the alias failed. Check the server and try again.'))
    }
  }

  async function handleRemove(aliasId: number): Promise<void> {
    setError(null)
    setRemovingId(aliasId)
    try {
      await removeAlias.mutateAsync({ id: ingredient.id, aliasId })
    } catch (caught) {
      setError(describeError(caught, 'Removing the alias failed. Check the server and try again.'))
    } finally {
      setRemovingId(null)
    }
  }

  return (
    <div className="reference-data__aliases">
      {ingredient.aliases.length === 0 ? (
        <span className="reference-data__alias-empty">—</span>
      ) : (
        <ul className="reference-data__alias-list">
          {ingredient.aliases.map((entry) => (
            <li key={entry.id}>
              <span>{entry.alias}</span>
              {canEdit && (
                <button
                  type="button"
                  aria-label={`Remove alias ${entry.alias} from ${name}`}
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
          <label className="sr-only" htmlFor={`ingredient-alias-${ingredient.id}`}>
            New alias for {name}
          </label>
          <input
            id={`ingredient-alias-${ingredient.id}`}
            type="text"
            autoComplete="off"
            value={alias}
            onChange={(event) => {
              setAlias(event.target.value)
              setError(null)
            }}
          />
          <button type="submit" aria-label={`Add alias to ${name}`} disabled={addAlias.isPending}>
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

/**
 * Ingredients tab (docs/API.md §22): canonical names, grocery departments,
 * aliases, retire/delete, and the merge workflow. Reads are open to every
 * authenticated actor; writes need `reference_data.edit` and hard delete is
 * Admin-only — the server enforces all of it.
 */
export function IngredientsTab() {
  const { canEdit, isAdmin } = useReferenceDataAccess()

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [includeRetired, setIncludeRetired] = useState(false)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const ingredientsQuery = useIngredients(search, includeRetired)
  const departmentsQuery = useDepartments('')
  const retireIngredient = useRetireIngredient()
  const deleteIngredient = useDeleteIngredient()

  const rows = ingredientsQuery.data ?? []
  const visible = includeRetired ? rows : rows.filter((ingredient) => ingredient.is_active)
  const departments = departmentsQuery.data ?? []
  const departmentNames = new Map(
    departments.map((department) => [department.id, department.name] as const),
  )
  const showActions = canEdit || isAdmin

  function departmentText(ingredient: Ingredient): string {
    if (ingredient.department_id === null) {
      return '—'
    }
    return departmentNames.get(ingredient.department_id) ?? `#${ingredient.department_id}`
  }

  function handleSearch(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    setSearch(searchInput.trim())
    setActionError(null)
  }

  function clearSearch(): void {
    setSearchInput('')
    setSearch('')
  }

  async function handleRetire(ingredient: Ingredient): Promise<void> {
    setActionError(null)
    try {
      await retireIngredient.mutateAsync(ingredient.id)
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
      await deleteIngredient.mutateAsync(dialog.ingredient.id)
      setDialog(null)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.IngredientInUse) {
        setDeleteError(inUseMessage(error.details))
        return
      }
      setDeleteError(describeError(error, 'Delete failed. Check the server and try again.'))
    }
  }

  if (ingredientsQuery.isPending) {
    return <LoadingScreen label="Loading ingredients…" />
  }

  if (ingredientsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load ingredients"
        error={ingredientsQuery.error}
        onRetry={() => void ingredientsQuery.refetch()}
      />
    )
  }

  return (
    <section
      className="settings-subsection reference-data__tab"
      aria-labelledby="ingredients-heading"
    >
      <div className="settings-section__header">
        <h4 id="ingredients-heading">Ingredients</h4>
        {canEdit && (
          <div className="reference-data__header-actions">
            <button
              type="button"
              onClick={() => {
                setActionError(null)
                setDialog({ kind: 'merge' })
              }}
            >
              Merge ingredients
            </button>
            <button
              type="button"
              className="button--primary"
              onClick={() => {
                setActionError(null)
                setDialog({ kind: 'create' })
              }}
            >
              Add ingredient
            </button>
          </div>
        )}
      </div>

      {actionError && (
        <p className="form__error" role="alert">
          {actionError}
        </p>
      )}

      <form className="reference-data__toolbar" onSubmit={handleSearch}>
        <div className="form__field reference-data__search">
          <label htmlFor="ingredient-search">Search ingredients</label>
          <input
            id="ingredient-search"
            type="search"
            value={searchInput}
            placeholder="Canonical name or alias…"
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
            ? 'No ingredients match this search.'
            : 'No ingredients yet. Add the first one to start building recipes.'}
        </p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Ingredients</caption>
            <thead>
              <tr>
                <th scope="col">Canonical name</th>
                <th scope="col">Department</th>
                <th scope="col">Aliases</th>
                <th scope="col">Status</th>
                {showActions && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((ingredient) => (
                <tr key={ingredient.id}>
                  <th scope="row">{ingredient.canonical_name}</th>
                  <td>{departmentText(ingredient)}</td>
                  <td>
                    <IngredientAliasEditor ingredient={ingredient} canEdit={canEdit} />
                  </td>
                  <td>{ingredient.is_active ? 'Active' : 'Retired'}</td>
                  {showActions && (
                    <td className="data-table__actions reference-data__actions">
                      {canEdit && (
                        <>
                          <button
                            type="button"
                            aria-label={`Edit ${ingredient.canonical_name}`}
                            onClick={() => {
                              setActionError(null)
                              setDialog({ kind: 'edit', ingredient })
                            }}
                          >
                            Edit
                          </button>
                          {ingredient.is_active && (
                            <button
                              type="button"
                              aria-label={`Retire ${ingredient.canonical_name}`}
                              disabled={retireIngredient.isPending}
                              onClick={() => void handleRetire(ingredient)}
                            >
                              Retire
                            </button>
                          )}
                        </>
                      )}
                      {isAdmin && (
                        <button
                          type="button"
                          aria-label={`Delete ${ingredient.canonical_name}`}
                          onClick={() => {
                            setDeleteError(null)
                            setDialog({ kind: 'delete', ingredient })
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
        <IngredientFormDialog
          mode="create"
          departments={departments}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'edit' && (
        <IngredientFormDialog
          mode="edit"
          ingredient={dialog.ingredient}
          departments={departments}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'merge' && <IngredientMergeDialog onClose={() => setDialog(null)} />}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.ingredient.canonical_name}?`}
          message={
            <p>
              This permanently removes the ingredient. Ingredients still referenced by recipes or
              merge records cannot be deleted — retire them instead so existing data keeps
              resolving. Retiring is one-way.
            </p>
          }
          confirmLabel="Delete ingredient"
          pending={deleteIngredient.isPending}
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

import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { Tag } from '../../api/referenceData'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useReferenceDataAccess } from './access'
import { TagFormDialog } from './TagFormDialog'
import { useDeleteTag, useTags } from './useReferenceData'

type DialogState = { kind: 'create' } | { kind: 'edit'; tag: Tag } | { kind: 'delete'; tag: Tag }

function inUseMessage(count: number): string {
  return count === 1
    ? '1 recipe uses this tag. Remove the tag from that recipe first, then delete it.'
    : `${count} recipes use this tag. Remove the tag from those recipes first, then delete it.`
}

function describeError(error: unknown, fallback: string): string {
  return isApiError(error) ? error.message : fallback
}

/**
 * Tags tab (docs/API.md §22): recipe tags for filtering and browsing. Tags
 * have no retirement flag in the M2 contract, so rows have no status column
 * and no retire/enable action — only rename (`reference_data.edit`) and the
 * Admin-only hard delete.
 */
export function TagsTab() {
  const { canEdit, isAdmin } = useReferenceDataAccess()

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const tagsQuery = useTags(search)
  const deleteTag = useDeleteTag()

  const visible = tagsQuery.data ?? []
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

  async function handleDelete(): Promise<void> {
    if (dialog?.kind !== 'delete') {
      return
    }
    setDeleteError(null)
    try {
      await deleteTag.mutateAsync(dialog.tag.id)
      setDialog(null)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.TagInUse) {
        const count = error.details['usage_count']
        setDeleteError(
          typeof count === 'number'
            ? inUseMessage(count)
            : 'This tag is still in use. Remove it from its recipes first.',
        )
        return
      }
      setDeleteError(describeError(error, 'Delete failed. Check the server and try again.'))
    }
  }

  if (tagsQuery.isPending) {
    return <LoadingScreen label="Loading tags…" />
  }

  if (tagsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load tags"
        error={tagsQuery.error}
        onRetry={() => void tagsQuery.refetch()}
      />
    )
  }

  return (
    <section className="settings-subsection reference-data__tab" aria-labelledby="tags-heading">
      <div className="settings-section__header">
        <h4 id="tags-heading">Tags</h4>
        {canEdit && (
          <button
            type="button"
            className="button--primary"
            onClick={() => {
              setActionError(null)
              setDialog({ kind: 'create' })
            }}
          >
            Add tag
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
          <label htmlFor="tag-search">Search tags</label>
          <input
            id="tag-search"
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
      </form>

      {visible.length === 0 ? (
        <p className="page-note">
          {search.length > 0
            ? 'No tags match this search.'
            : 'No tags yet. Add the first one to start labeling recipes.'}
        </p>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Tags</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                {showActions && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((tag) => (
                <tr key={tag.id}>
                  <th scope="row">{tag.name}</th>
                  {showActions && (
                    <td className="data-table__actions reference-data__actions">
                      {canEdit && (
                        <button
                          type="button"
                          aria-label={`Rename ${tag.name}`}
                          onClick={() => {
                            setActionError(null)
                            setDialog({ kind: 'edit', tag })
                          }}
                        >
                          Rename
                        </button>
                      )}
                      {isAdmin && (
                        <button
                          type="button"
                          aria-label={`Delete ${tag.name}`}
                          onClick={() => {
                            setDeleteError(null)
                            setDialog({ kind: 'delete', tag })
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
        <TagFormDialog
          mode="create"
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'edit' && (
        <TagFormDialog
          mode="edit"
          tag={dialog.tag}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.tag.name}?`}
          message={
            <p>
              This permanently removes the tag. Tags still used by recipes cannot be deleted —
              remove the tag from those recipes first. Tags have no retirement state.
            </p>
          }
          confirmLabel="Delete tag"
          pending={deleteTag.isPending}
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

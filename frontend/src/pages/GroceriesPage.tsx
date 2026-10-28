import { useMemo, useState, type JSX } from 'react'
import { Link } from 'react-router-dom'

import type { GroceryItem, GroceryRangePreset } from '../api/groceries'
import { ErrorScreen, LoadingScreen } from '../components/Screens'
import { ConfirmDialog } from '../components/Modal'
import { useGroceriesAccess } from '../features/groceries/access'
import { describeGroceryError } from '../features/groceries/groceryFormat'
import { GroceryList } from '../features/groceries/GroceryList'
import { ManualItemDialog } from '../features/groceries/ManualItemDialog'
import { manualSeedFromItem, type ManualItemSeed } from '../features/groceries/manualItem'
import { RangeSelector } from '../features/groceries/RangeSelector'
import { StaplesPanel } from '../features/groceries/StaplesPanel'
import {
  useDeleteGroceryManualItem,
  useGroceryDepartmentOptions,
  useGroceryIngredientOptions,
  useGroceryList,
  useGroceryPreferences,
  useGroceryUnitOptions,
  usePatchGroceryPreferences,
  useSetGroceryChecked,
} from '../features/groceries/useGroceries'

type FixedPreset = Exclude<GroceryRangePreset, 'custom'>

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; seed: ManualItemSeed }
  | { kind: 'delete'; item: GroceryItem }

/**
 * Groceries page: the live projection over
 * the active range, with a range selector, a per-user hide-checked toggle
 * manual-item add/edit/delete, and household staples. `groceries.view` gates
 * the content; `groceries.edit` only hides/disables writes — the server is the
 * sole authorization gate.
 */
export function GroceriesPage(): JSX.Element {
  const { canView, canEdit } = useGroceriesAccess()

  const [hideCheckedOverride, setHideCheckedOverride] = useState<boolean | null>(null)
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [staplesOpen, setStaplesOpen] = useState(false)
  const [announcement, setAnnouncement] = useState('')
  const [rangeError, setRangeError] = useState<string | null>(null)
  const [checkError, setCheckError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [preferencesError, setPreferencesError] = useState<string | null>(null)
  const [pendingIdentities, setPendingIdentities] = useState<ReadonlySet<string>>(() => new Set())

  const listQuery = useGroceryList(canView)
  const preferencesQuery = useGroceryPreferences(canView)
  const patchPreferences = usePatchGroceryPreferences()
  const setChecked = useSetGroceryChecked()
  const deleteManualItem = useDeleteGroceryManualItem()

  const departmentsQuery = useGroceryDepartmentOptions(canView)
  const unitsQuery = useGroceryUnitOptions(canView)
  const ingredientsQuery = useGroceryIngredientOptions(canView)

  // The server holds the remembered range and the hide-checked preference.
  // Until the user toggles, the toggle reflects the stored preference; a local
  // override (reverted on failure) keeps the toggle responsive.
  const hideChecked = hideCheckedOverride ?? preferencesQuery.data?.hide_checked ?? false

  const range = listQuery.data?.range ?? null

  const visibleDepartments = useMemo(() => {
    const departments = listQuery.data?.departments ?? []
    if (!hideChecked) {
      return departments
    }
    return departments
      .map((department) => ({
        ...department,
        items: department.items.filter((item) => !item.checked),
      }))
      .filter((department) => department.items.length > 0)
  }, [listQuery.data, hideChecked])

  const visibleItemCount = visibleDepartments.reduce(
    (total, department) => total + department.items.length,
    0,
  )

  // The print-entry affordance appears whenever the range has any items at all
  // (checked lines included), even if they are all hidden by hide-checked.
  const totalItemCount = (listQuery.data?.departments ?? []).reduce(
    (total, department) => total + department.items.length,
    0,
  )

  function handleSelectPreset(preset: FixedPreset): void {
    setRangeError(null)
    patchPreferences.mutate(
      { range_preset: preset, range_start: null, range_days: null },
      {
        onError: (error) =>
          setRangeError(describeGroceryError(error, 'Changing the range failed. Try again.')),
      },
    )
  }

  function handleSelectCustom(start: string, days: number): void {
    setRangeError(null)
    patchPreferences.mutate(
      { range_preset: 'custom', range_start: start, range_days: days },
      {
        onError: (error) =>
          setRangeError(describeGroceryError(error, 'Changing the range failed. Try again.')),
      },
    )
  }

  function handleToggleHideChecked(next: boolean): void {
    setPreferencesError(null)
    setHideCheckedOverride(next)
    patchPreferences.mutate(
      { hide_checked: next },
      {
        onError: (error) => {
          // Revert the optimistic toggle and surface why it did not stick.
          setHideCheckedOverride(null)
          setPreferencesError(
            describeGroceryError(error, 'Saving the hide-checked preference failed. Try again.'),
          )
        },
      },
    )
  }

  function markIdentityPending(identity: string, pending: boolean): void {
    setPendingIdentities((current) => {
      const next = new Set(current)
      if (pending) {
        next.add(identity)
      } else {
        next.delete(identity)
      }
      return next
    })
  }

  async function handleToggleChecked(item: GroceryItem, checked: boolean): Promise<void> {
    setCheckError(null)
    markIdentityPending(item.identity, true)
    try {
      await setChecked.mutateAsync({ identity: item.identity, checked })
      setAnnouncement(
        checked ? `Marked ${item.name} as bought.` : `Marked ${item.name} as not bought.`,
      )
    } catch (error) {
      setCheckError(describeGroceryError(error, `Could not update ${item.name}. Try again.`))
    } finally {
      markIdentityPending(item.identity, false)
    }
  }

  async function handleDelete(): Promise<void> {
    if (dialog?.kind !== 'delete' || dialog.item.manual_item_id === null) {
      return
    }
    setDeleteError(null)
    try {
      await deleteManualItem.mutateAsync(dialog.item.manual_item_id)
      setAnnouncement(`Deleted ${dialog.item.name}.`)
      setDialog(null)
    } catch (error) {
      setDeleteError(describeGroceryError(error, 'Delete failed. Try again.'))
    }
  }

  if (!canView) {
    return (
      <section className="page grocery-page" aria-labelledby="groceries-heading">
        <h2 id="groceries-heading" tabIndex={-1}>
          Groceries
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to view groceries. Ask an Admin for the groceries.view
          permission.
        </p>
      </section>
    )
  }

  const departments = departmentsQuery.data ?? []
  const units = unitsQuery.data ?? []
  const ingredients = ingredientsQuery.data ?? []

  return (
    <section className="page grocery-page" aria-labelledby="groceries-heading">
      <div className="grocery-toolbar">
        <h2 id="groceries-heading" tabIndex={-1}>
          Groceries
        </h2>
        <div className="grocery-toolbar__actions">
          {listQuery.data !== undefined && totalItemCount > 0 && (
            <Link to="/groceries/print" className="grocery-toolbar__print">
              Print
            </Link>
          )}
          {canEdit && (
            <button
              type="button"
              className="button--primary"
              disabled={range === null}
              onClick={() => {
                setAnnouncement('')
                setDialog({ kind: 'create' })
              }}
            >
              Add grocery item
            </button>
          )}
        </div>
      </div>

      <RangeSelector
        range={range}
        pending={patchPreferences.isPending}
        onSelectPreset={handleSelectPreset}
        onSelectCustom={handleSelectCustom}
      />

      <div className="grocery-controls">
        <label className="reference-data__checkbox grocery-controls__hide">
          <input
            type="checkbox"
            checked={hideChecked}
            onChange={(event) => handleToggleHideChecked(event.currentTarget.checked)}
          />
          Hide checked items
        </label>
        <button
          type="button"
          className="grocery-controls__staples"
          aria-expanded={staplesOpen}
          aria-controls="grocery-staples-panel"
          onClick={() => setStaplesOpen((open) => !open)}
        >
          Staples
        </button>
      </div>

      <p className="sr-only" role="status" aria-live="polite">
        {announcement.length > 0
          ? announcement
          : `${visibleItemCount} ${visibleItemCount === 1 ? 'item' : 'items'} shown`}
      </p>

      {rangeError !== null && (
        <p className="form__error" role="alert">
          {rangeError}
        </p>
      )}
      {checkError !== null && (
        <p className="form__error" role="alert">
          {checkError}
        </p>
      )}
      {preferencesError !== null && (
        <p className="form__error" role="alert">
          {preferencesError}
        </p>
      )}

      {listQuery.isPending && <LoadingScreen label="Loading groceries…" />}

      {listQuery.isError && (
        <ErrorScreen
          title="Cannot load groceries"
          error={listQuery.error}
          onRetry={() => void listQuery.refetch()}
        />
      )}

      {listQuery.data &&
        (visibleDepartments.length === 0 ? (
          <p className="page-note">
            {hideChecked && (listQuery.data.departments.length > 0 || visibleItemCount === 0)
              ? 'No items to show. Turn off “Hide checked items” to see checked items.'
              : 'No groceries for this range. Add a manual item or plan meals with recipes.'}
          </p>
        ) : (
          <GroceryList
            departments={visibleDepartments}
            canEdit={canEdit}
            pendingIdentities={pendingIdentities}
            onToggle={(item, checked) => void handleToggleChecked(item, checked)}
            onEditManual={(item) => {
              const seed = manualSeedFromItem(item)
              if (seed) {
                setAnnouncement('')
                setDialog({ kind: 'edit', seed })
              }
            }}
            onDeleteManual={(item) => {
              setDeleteError(null)
              setDialog({ kind: 'delete', item })
            }}
          />
        ))}

      {staplesOpen && (
        <div id="grocery-staples-panel">
          <StaplesPanel
            canEdit={canEdit}
            range={range}
            departments={departments}
            units={units}
            ingredients={ingredients}
            onAnnounce={setAnnouncement}
          />
        </div>
      )}

      {dialog?.kind === 'create' && range && (
        <ManualItemDialog
          mode="create"
          range={range}
          departments={departments}
          units={units}
          ingredients={ingredients}
          onClose={() => setDialog(null)}
          onSaved={(name) => {
            setAnnouncement(`Added ${name}.`)
            setDialog(null)
          }}
        />
      )}

      {dialog?.kind === 'edit' && range && (
        <ManualItemDialog
          mode="edit"
          seed={dialog.seed}
          range={range}
          departments={departments}
          units={units}
          ingredients={ingredients}
          onClose={() => setDialog(null)}
          onSaved={(name) => {
            setAnnouncement(`Saved ${name}.`)
            setDialog(null)
          }}
        />
      )}

      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={`Delete ${dialog.item.name}?`}
          message={<p>This removes the manual item from the current list.</p>}
          confirmLabel="Delete item"
          pending={deleteManualItem.isPending}
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

import { useState, type FormEvent } from 'react'

import {
  CALENDAR_ENTRY_LABELS,
  CALENDAR_ENTRY_TYPES,
  CALENDAR_ICONS,
  ICON_ENTRY_TYPES,
  type CalendarEntryType,
  type CalendarWriteRequest,
} from '../../api/calendar'
import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import { Modal } from '../../components/Modal'
import { useRecipeList } from '../recipes/useRecipes'
import { fullDateLabel } from './calendarDates'
import { EventIcon } from './icons'
import {
  useCalendarDay,
  useCalendarRepeatCheck,
  useDeleteCalendarDay,
  useSaveCalendarDay,
  useSetCalendarLock,
} from './useCalendar'

const TYPE_LABELS = CALENDAR_ENTRY_TYPES.map((type) => ({
  value: type,
  label: CALENDAR_ENTRY_LABELS[type],
}))

/**
 * Date detail editor (M5-11). A modal that creates/replaces, deletes, or
 * locks a date's entry. Historical dates render read-only (M5-12); the server
 * enforces that independently.
 */
export function DateDetailDialog({
  date,
  canEdit,
  onClose,
}: {
  date: string
  canEdit: boolean
  onClose: () => void
}) {
  const dayQuery = useCalendarDay(date)
  const day = dayQuery.data
  const saveMutation = useSaveCalendarDay()
  const deleteMutation = useDeleteCalendarDay()
  const lockMutation = useSetCalendarLock()

  const [entryType, setEntryType] = useState<CalendarEntryType>('recipe')
  const [recipeId, setRecipeId] = useState<number | null>(null)
  const [recipeName, setRecipeName] = useState<string | null>(null)
  const [recipeSearch, setRecipeSearch] = useState('')
  const [dinerCount, setDinerCount] = useState('')
  const [restaurantName, setRestaurantName] = useState('')
  const [locationText, setLocationText] = useState('')
  const [customLabel, setCustomLabel] = useState('')
  const [notes, setNotes] = useState('')
  const [iconKey, setIconKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loadedKey, setLoadedKey] = useState<string | null>(null)

  const entry = day?.entry ?? null
  const isHistorical = day?.is_historical ?? false
  const readOnly = isHistorical || !canEdit

  // Seed the form from the loaded day. Adjusting state during render is React's
  // documented derive-from-props pattern and avoids a state-syncing effect.
  const entryKey = day ? `${day.date}:${day.entry?.updated_at ?? 'new'}` : null
  if (day && entryKey !== null && entryKey !== loadedKey) {
    const current = day.entry
    setLoadedKey(entryKey)
    setEntryType(current?.entry_type ?? 'recipe')
    setRecipeId(current?.recipe?.id ?? null)
    setRecipeName(current?.recipe?.name ?? null)
    setRecipeSearch('')
    setDinerCount(current?.diner_count != null ? String(current.diner_count) : '')
    setRestaurantName(current?.restaurant_name ?? '')
    setLocationText(current?.location_text ?? '')
    setCustomLabel(current?.custom_label ?? '')
    setNotes(current?.notes ?? '')
    setIconKey(current?.icon_key ?? '')
    setError(null)
  }

  const repeatCheck = useCalendarRepeatCheck(date, recipeId)
  const recipeResults = useRecipeList(
    recipeSearch.trim().length >= 2 ? { search: recipeSearch, limit: 5 } : { limit: 5 },
  )

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setError(null)
    const request: CalendarWriteRequest = {
      entry_type: entryType,
      recipe_id: recipeId,
      diner_count: dinerCount.trim() === '' ? null : Number(dinerCount),
      restaurant_name: restaurantName.trim() === '' ? null : restaurantName,
      location_text: locationText.trim() === '' ? null : locationText,
      custom_label: customLabel.trim() === '' ? null : customLabel,
      notes: notes.trim() === '' ? null : notes,
      icon_key: iconKey === '' ? null : iconKey,
    }
    try {
      await saveMutation.mutateAsync({ date, request })
      onClose()
    } catch (caught) {
      setError(describeSaveError(caught))
    }
  }

  async function handleDelete(): Promise<void> {
    setError(null)
    try {
      await deleteMutation.mutateAsync(date)
      onClose()
    } catch (caught) {
      setError(isApiError(caught) ? caught.message : 'Deleting failed. Try again.')
    }
  }

  async function handleLockToggle(): Promise<void> {
    if (!entry) {
      return
    }
    setError(null)
    try {
      await lockMutation.mutateAsync({ date, locked: !entry.is_locked })
    } catch (caught) {
      setError(isApiError(caught) ? caught.message : 'Changing the lock failed. Try again.')
    }
  }

  const pending = saveMutation.isPending || deleteMutation.isPending || lockMutation.isPending

  const repeatWarning = repeatCheck.data?.is_repeat ? repeatCheck.data : null

  const title = `${fullDateLabel(date)}${isHistorical ? ' — read-only' : ''}`

  return (
    <Modal title={title} onClose={onClose}>
      {dayQuery.isLoading && <p className="page-note">Loading…</p>}
      {dayQuery.isError && (
        <p className="form__error" role="alert">
          Could not load this date.
        </p>
      )}

      {day && readOnly && (
        <div className="calendar-detail__readonly">
          {entry ? (
            <>
              <p className="calendar-detail__summary">
                {entry.icon_key && (
                  <EventIcon iconKey={entry.icon_key} className="calendar-detail__icon" />
                )}
                <strong>{entry.display_text}</strong>
                {entry.is_locked && <span aria-label="Locked"> 🔒</span>}
              </p>
              <p className="page-note">{CALENDAR_ENTRY_LABELS[entry.entry_type]}</p>
              {entry.notes && <p className="calendar-detail__notes">{entry.notes}</p>}
            </>
          ) : (
            <p className="page-note">Nothing planned.</p>
          )}
          {isHistorical && (
            <p className="page-note">
              Past dates are read-only. An Admin can correct history from the administration
              workflow.
            </p>
          )}
        </div>
      )}

      {day && !readOnly && (
        <form
          className="form calendar-detail"
          onSubmit={(event) => void handleSubmit(event)}
          noValidate
        >
          {error !== null && (
            <p className="form__error" role="alert">
              {error}
            </p>
          )}

          <label className="calendar-detail__field" htmlFor="calendar-entry-type">
            <span>Type</span>
            <select
              id="calendar-entry-type"
              value={entryType}
              onChange={(event) => {
                setEntryType(event.target.value as CalendarEntryType)
                if (!ICON_ENTRY_TYPES.includes(event.target.value as CalendarEntryType)) {
                  setIconKey('')
                }
              }}
            >
              {TYPE_LABELS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>

          {(entryType === 'recipe' ||
            entryType === 'host_family_dinner' ||
            entryType === 'holiday' ||
            entryType === 'misc') && (
            <div className="calendar-detail__field">
              <label htmlFor="calendar-recipe-search">
                {entryType === 'recipe' ? 'Recipe (required)' : 'Recipe (optional)'}
              </label>
              <input
                id="calendar-recipe-search"
                type="text"
                autoComplete="off"
                placeholder="Search recipes"
                value={recipeSearch}
                onChange={(event) => setRecipeSearch(event.target.value)}
              />
              {recipeSearch.trim().length >= 2 && (recipeResults.data?.items ?? []).length > 0 && (
                <ul className="calendar-detail__results">
                  {(recipeResults.data?.items ?? []).map((candidate) => (
                    <li key={candidate.id}>
                      <button
                        type="button"
                        onClick={() => {
                          setRecipeId(candidate.id)
                          setRecipeName(candidate.name)
                          setRecipeSearch('')
                        }}
                      >
                        {candidate.image && (
                          <img
                            className="calendar-detail__thumb"
                            src={candidate.image.url}
                            alt=""
                          />
                        )}
                        <span className="calendar-detail__result-name">{candidate.name}</span>
                        <span className="calendar-detail__result-meta">
                          {candidate.last_eaten
                            ? `Last eaten ${candidate.last_eaten}`
                            : 'Not eaten yet'}
                          {candidate.times_eaten != null && ` · ${candidate.times_eaten}×`}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              {recipeId !== null && (
                <p className="form__hint">
                  Selected: <strong>{recipeName}</strong>{' '}
                  <button
                    type="button"
                    onClick={() => {
                      setRecipeId(null)
                      setRecipeName(null)
                    }}
                  >
                    Clear
                  </button>
                </p>
              )}
              {repeatWarning && (
                <p className="form__notice" role="status">
                  This recipe also appears on{' '}
                  {repeatWarning.occurrences.map((occurrence) => occurrence.date).join(', ')} in the
                  repeat window. You can still save it.
                </p>
              )}
            </div>
          )}

          {(entryType === 'host_family_dinner' ||
            entryType === 'holiday' ||
            entryType === 'misc') && (
            <label className="calendar-detail__field" htmlFor="calendar-custom-label">
              <span>{entryType === 'misc' ? 'Label (required)' : 'Name (optional)'}</span>
              <input
                id="calendar-custom-label"
                type="text"
                autoComplete="off"
                value={customLabel}
                onChange={(event) => setCustomLabel(event.target.value)}
              />
            </label>
          )}

          {entryType === 'eat_out' && (
            <label className="calendar-detail__field" htmlFor="calendar-restaurant">
              <span>Restaurant (optional)</span>
              <input
                id="calendar-restaurant"
                type="text"
                autoComplete="off"
                value={restaurantName}
                onChange={(event) => setRestaurantName(event.target.value)}
              />
            </label>
          )}

          {entryType === 'go_to_family_dinner' && (
            <label className="calendar-detail__field" htmlFor="calendar-location">
              <span>Host / location (optional)</span>
              <input
                id="calendar-location"
                type="text"
                autoComplete="off"
                value={locationText}
                onChange={(event) => setLocationText(event.target.value)}
              />
            </label>
          )}

          {ICON_ENTRY_TYPES.includes(entryType) && (
            <label className="calendar-detail__field" htmlFor="calendar-icon">
              <span>Icon (optional)</span>
              <select
                id="calendar-icon"
                value={iconKey}
                onChange={(event) => setIconKey(event.target.value)}
              >
                <option value="">No icon</option>
                {CALENDAR_ICONS.map((icon) => (
                  <option key={icon} value={icon}>
                    {icon}
                  </option>
                ))}
              </select>
            </label>
          )}

          {entryType === 'host_family_dinner' && (
            <label className="calendar-detail__field" htmlFor="calendar-diner-count">
              <span>Diner count (optional)</span>
              <input
                id="calendar-diner-count"
                type="text"
                inputMode="numeric"
                value={dinerCount}
                onChange={(event) => setDinerCount(event.target.value)}
              />
            </label>
          )}

          <label className="calendar-detail__field" htmlFor="calendar-notes">
            <span>Notes (optional)</span>
            <textarea
              id="calendar-notes"
              rows={3}
              value={notes}
              onChange={(event) => setNotes(event.target.value)}
            />
          </label>

          <div className="modal__actions calendar-detail__actions">
            {entry && (
              <button type="button" onClick={() => void handleLockToggle()} disabled={pending}>
                {entry.is_locked ? 'Unlock' : 'Lock'}
              </button>
            )}
            {entry && (
              <button type="button" onClick={() => void handleDelete()} disabled={pending}>
                Delete
              </button>
            )}
            <button type="button" onClick={onClose} disabled={pending}>
              Cancel
            </button>
            <button type="submit" className="button--primary" disabled={pending}>
              {pending ? 'Saving…' : 'Save'}
            </button>
          </div>
        </form>
      )}
    </Modal>
  )
}

function describeSaveError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.CalendarRecipeRequired:
        return 'Choose a recipe for this date.'
      case ErrorCodes.CalendarMiscLabelRequired:
        return 'Enter a label for this misc entry.'
      case ErrorCodes.CalendarDateInPast:
        return 'This date is in the past and cannot be changed.'
      case ErrorCodes.ValidationFailed:
        return 'Check the fields and try again.'
      default:
        return caught.message
    }
  }
  return 'Saving failed. Try again.'
}

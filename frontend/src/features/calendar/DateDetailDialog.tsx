import { useState, type FormEvent } from 'react'

import { CALENDAR_ENTRY_LABELS, type CalendarOperation } from '../../api/calendar'
import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import { Modal } from '../../components/Modal'
import { EventIcon } from './icons'
import { CalendarEntryFields } from './CalendarEntryFields'
import {
  EMPTY_CALENDAR_ENTRY_FIELDS,
  calendarEntryFieldsFromEntry,
  toCalendarWriteRequest,
  type CalendarEntryFieldsValue,
} from './calendarEntryFields'
import { HistoricalCorrectionDialog } from './HistoricalCorrectionDialog'
import { RerollControl } from './RerollControl'
import { fullDateLabel } from './calendarDates'
import {
  useCalendarDay,
  useCalendarRepeatCheck,
  useDeleteCalendarDay,
  useSaveCalendarDay,
  useSetCalendarLock,
} from './useCalendar'

/**
 * Date detail editor (M5-11). A modal that creates/replaces, deletes, or
 * locks a date's entry. Historical dates render read-only (M5-12); an Admin can
 * launch the M12-07 historical-correction flow from here. The server enforces
 * past-date immutability and authorization independently.
 */
export function DateDetailDialog({
  date,
  canEdit,
  isAdmin = false,
  onClose,
  onRerolled,
  onCorrected,
}: {
  date: string
  canEdit: boolean
  /** UI visibility only (from `useAdminAccess`): the server is the authority. */
  isAdmin?: boolean
  onClose: () => void
  onRerolled: (operation: CalendarOperation) => void
  /** Called after a successful historical correction, before the dialog closes. */
  onCorrected?: () => void
}) {
  const dayQuery = useCalendarDay(date)
  const day = dayQuery.data
  const saveMutation = useSaveCalendarDay()
  const deleteMutation = useDeleteCalendarDay()
  const lockMutation = useSetCalendarLock()

  const [fields, setFields] = useState<CalendarEntryFieldsValue>(EMPTY_CALENDAR_ENTRY_FIELDS)
  const [error, setError] = useState<string | null>(null)
  const [loadedKey, setLoadedKey] = useState<string | null>(null)
  const [correcting, setCorrecting] = useState(false)

  const entry = day?.entry ?? null
  const isHistorical = day?.is_historical ?? false
  const readOnly = isHistorical || !canEdit

  // Seed the form from the loaded day. Adjusting state during render is React's
  // documented derive-from-props pattern and avoids a state-syncing effect.
  const entryKey = day ? `${day.date}:${day.entry?.updated_at ?? 'new'}` : null
  if (day && entryKey !== null && entryKey !== loadedKey) {
    setLoadedKey(entryKey)
    setFields(calendarEntryFieldsFromEntry(day.entry))
    setError(null)
  }

  const repeatCheck = useCalendarRepeatCheck(date, fields.recipeId)

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setError(null)
    try {
      await saveMutation.mutateAsync({ date, request: toCalendarWriteRequest(fields) })
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

  function handleRerolled(operation: CalendarOperation): void {
    onRerolled(operation)
    // Close so the page-level Undo banner is visible and focusable (the modal
    // marks the rest of the page inert while open).
    onClose()
  }

  // The correction flow replaces this dialog (one modal at a time), so the
  // shared Modal keeps a single focus trap.
  if (correcting) {
    return (
      <HistoricalCorrectionDialog
        date={date}
        onClose={() => setCorrecting(false)}
        onCorrected={() => {
          onCorrected?.()
          onClose()
        }}
      />
    )
  }

  const pending = saveMutation.isPending || deleteMutation.isPending || lockMutation.isPending

  const repeatWarning =
    repeatCheck.data?.is_repeat === true ? (
      <p className="form__notice" role="status">
        This recipe also appears on{' '}
        {repeatCheck.data.occurrences.map((occurrence) => occurrence.date).join(', ')} in the repeat
        window. You can still save it.
      </p>
    ) : null

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
            <>
              <p className="page-note">
                Past dates are read-only. An Admin can correct history; every correction is audited.
              </p>
              {isAdmin && (
                <div className="modal__actions calendar-detail__actions">
                  <button type="button" onClick={() => setCorrecting(true)}>
                    Correct this date
                  </button>
                </div>
              )}
            </>
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

          <CalendarEntryFields
            value={fields}
            repeatWarning={repeatWarning}
            onChange={(patch) => setFields((current) => ({ ...current, ...patch }))}
          />

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

      {day && <RerollControl day={day} canEdit={canEdit} onRerolled={handleRerolled} />}
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

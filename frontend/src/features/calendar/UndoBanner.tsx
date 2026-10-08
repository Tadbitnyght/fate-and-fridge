import type { CalendarOperation } from '../../api/calendar'
import { describeUndoError } from './calendarErrors'
import { useUndoCalendarOperation } from './useCalendar'

/**
 * Dismissible Undo banner shown after a successful reroll (M6-10). Undo
 * restores the prior state; Dismiss hides the banner without undoing. A failed
 * undo (UNDO_CONFLICT/UNDO_EXPIRED) reports a non-blocking message and clears
 * the banner rather than crashing.
 */
export function UndoBanner({
  operation,
  onDismiss,
  onError,
}: {
  operation: CalendarOperation
  onDismiss: () => void
  onError: (message: string) => void
}) {
  const undoMutation = useUndoCalendarOperation()

  async function handleUndo(): Promise<void> {
    try {
      await undoMutation.mutateAsync(operation.id)
      onDismiss()
    } catch (caught) {
      onError(describeUndoError(caught))
      onDismiss()
    }
  }

  return (
    <div className="undo-banner">
      <p className="undo-banner__message" role="status">
        Meal rerolled.
      </p>
      <div className="undo-banner__actions">
        <button type="button" onClick={() => void handleUndo()} disabled={undoMutation.isPending}>
          {undoMutation.isPending ? 'Undoing…' : 'Undo'}
        </button>
        <button type="button" onClick={onDismiss} disabled={undoMutation.isPending}>
          Dismiss
        </button>
      </div>
    </div>
  )
}

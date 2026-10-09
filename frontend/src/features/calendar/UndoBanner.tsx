import type { CalendarOperation } from '../../api/calendar'
import { describeBatchUndoError, describeUndoError } from './calendarErrors'
import { useUndoBatchOperation, useUndoCalendarOperation } from './useCalendar'

/**
 * Dismissible Undo banner shown after a successful reroll (M6-10) or a batch
 * apply (M7-09). Undo restores the prior state; Dismiss hides the banner
 * without undoing. A failed undo (UNDO_CONFLICT/UNDO_EXPIRED/…) reports a
 * non-blocking message and clears the banner rather than crashing.
 */
export function UndoBanner({
  operation,
  kind = 'single',
  onDismiss,
  onError,
}: {
  operation: CalendarOperation
  /** `single` undoes one reroll (M6); `batch` undoes a whole apply (M7). */
  kind?: 'single' | 'batch'
  onDismiss: () => void
  onError: (message: string) => void
}) {
  const singleUndo = useUndoCalendarOperation()
  const batchUndo = useUndoBatchOperation()
  const undoMutation = kind === 'batch' ? batchUndo : singleUndo

  async function handleUndo(): Promise<void> {
    try {
      await undoMutation.mutateAsync(operation.id)
      onDismiss()
    } catch (caught) {
      onError(kind === 'batch' ? describeBatchUndoError(caught) : describeUndoError(caught))
      onDismiss()
    }
  }

  return (
    <div className="undo-banner">
      <p className="undo-banner__message" role="status">
        {kind === 'batch' ? 'Batch reroll applied.' : 'Meal rerolled.'}
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

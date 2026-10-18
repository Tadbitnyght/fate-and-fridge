import { useEffect, useRef } from 'react'

import type { BatchRerollMode, BatchUnchangedReason, CalendarOperation } from '../../api/calendar'
import { formatHouseholdDate } from '../../lib/dateFormat'
import { Modal } from '../../components/Modal'
import { BATCH_MODE_LABELS } from './batchModes'
import { fullDateLabel } from './calendarDates'
import { describeBatchApplyError, describeBatchPreviewError } from './calendarErrors'
import { useApplyBatchReroll, usePreviewBatchReroll } from './useCalendar'

const UNCHANGED_LABELS: Record<BatchUnchangedReason, string> = {
  locked: 'Locked',
  vacation: 'Vacation',
  special: 'Special event',
  existing_non_recipe: 'Already planned',
}

export interface BatchAppliedResult {
  /** The batch operation to undo, or null when the apply changed nothing. */
  operation: CalendarOperation | null
  changedCount: number
}

/**
 * Batch reroll preview wizard (M7-09). Opens for a chosen range, generates a
 * persisted preview draft on mount, and shows the per-date diff: proposed dates
 * are highlighted while unchanged/locked/special/vacation dates are muted with
 * their `unchanged_reason`. "Try Another" re-previews with fresh randomness (a
 * new draft token); "Apply" commits the shown result atomically; "Cancel"
 * discards the preview. Backend failures render non-blockingly.
 */
export function BatchPreviewDialog({
  mode,
  startDate,
  onClose,
  onApplied,
}: {
  mode: BatchRerollMode
  startDate: string
  onClose: () => void
  onApplied: (result: BatchAppliedResult) => void
}) {
  const preview = usePreviewBatchReroll()
  const apply = useApplyBatchReroll()
  const startedRef = useRef(false)

  useEffect(() => {
    // Kick off the first preview once the dialog mounts. A ref guard keeps this
    // idempotent under React StrictMode's double effect invocation.
    if (startedRef.current) {
      return
    }
    startedRef.current = true
    preview.mutate({ startDate, mode })
  }, [preview, startDate, mode])

  const data = preview.data ?? null
  const pending = preview.isPending || apply.isPending
  const previewError = preview.isError ? describeBatchPreviewError(preview.error) : null
  const applyError = apply.isError ? describeBatchApplyError(apply.error) : null
  const nothingToChange =
    data !== null && data.days.length > 0 && data.days.every((day) => day.status === 'unchanged')

  function handleTryAnother(): void {
    apply.reset()
    preview.mutate({ startDate, mode })
  }

  async function handleApply(): Promise<void> {
    const token = data?.draft.token
    if (!token) {
      return
    }
    try {
      const result = await apply.mutateAsync(token)
      onApplied({
        operation: result.changed_count > 0 ? result.operation : null,
        changedCount: result.changed_count,
      })
    } catch {
      // The mutation exposes the error; describeBatchApplyError renders it.
    }
  }

  return (
    <Modal title={`Reroll preview — ${BATCH_MODE_LABELS[mode]}`} onClose={onClose}>
      {preview.isPending && (
        <p className="page-note" role="status">
          Generating preview…
        </p>
      )}

      {previewError !== null && (
        <p className="form__error" role="alert">
          {previewError}
        </p>
      )}
      {applyError !== null && (
        <p className="form__error" role="alert">
          {applyError}
        </p>
      )}

      {data !== null && (
        <>
          <p className="page-note">
            {formatHouseholdDate(data.draft.start_date)} →{' '}
            {formatHouseholdDate(data.draft.end_date)}
          </p>
          {nothingToChange && (
            <p className="form__notice" role="status">
              Nothing to change.
            </p>
          )}
          <ul className="batch-preview__days" aria-label="Proposed changes">
            {data.days.map((day) => {
              const changed = day.status === 'proposed'
              return (
                <li
                  key={day.date}
                  className={
                    changed
                      ? 'batch-preview__day batch-preview__day--changed'
                      : 'batch-preview__day batch-preview__day--unchanged'
                  }
                >
                  <span className="batch-preview__date">{fullDateLabel(day.date)}</span>
                  {changed ? (
                    <span className="batch-preview__change">
                      {day.current && (
                        <span className="batch-preview__before">{day.current.display_text} → </span>
                      )}
                      <strong>{day.proposed?.display_text}</strong>
                    </span>
                  ) : (
                    <span className="batch-preview__unchanged">
                      <span className="batch-preview__badge">
                        {day.unchanged_reason
                          ? UNCHANGED_LABELS[day.unchanged_reason]
                          : 'Unchanged'}
                      </span>
                      {day.current && (
                        <span className="batch-preview__current"> {day.current.display_text}</span>
                      )}
                    </span>
                  )}
                </li>
              )
            })}
          </ul>
        </>
      )}

      <div className="modal__actions">
        <button type="button" onClick={handleTryAnother} disabled={pending}>
          Try Another
        </button>
        <button type="button" onClick={onClose} disabled={pending}>
          Cancel
        </button>
        <button
          type="button"
          className="button--primary"
          onClick={() => void handleApply()}
          disabled={pending || data === null}
        >
          {apply.isPending ? 'Applying…' : 'Apply'}
        </button>
      </div>
    </Modal>
  )
}
